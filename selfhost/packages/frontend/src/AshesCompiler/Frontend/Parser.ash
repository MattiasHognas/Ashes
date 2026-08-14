import Ashes.Collection.List.append as appendList
import Ashes.Collection.List.reverse as reverseList
import Ashes.Text.join
import AshesCompiler.Frontend.Lexer
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Frontend.Token
export (
    type ExpressionParseResult(..),
    value parseExpression,
)

type ExpressionParseResult =
    | expression: Expr
    | diagnostics: List(DiagnosticEntry)

type alias ParserState = (List(Token), List(DiagnosticEntry))

let parserSyntheticToken kind position = Token(kind = kind, text = "", intValue = 0, floatValue = 0.0, position = position, length = 0)

let parserCurrent (state: ParserState) =
    match state with
        | (token :: _, _diagnostics) -> token
        | ([], _diagnostics) -> parserSyntheticToken(EOF)(0)

let parserAdvance (state: ParserState) =
    match state with
        | (token :: tail, diagnostics) -> (token, (tail, diagnostics))
        | ([], _diagnostics) -> (parserSyntheticToken(EOF)(0), state)

let parserDiagnostic (state: ParserState) (token: Token) (message: Str) =
    (let diagnostic = DiagnosticEntry(span = tokenSpan(token), message = message, code = Some("ASH003"))
    in
        match state with
            | (tokens, diagnostics) -> (tokens, diagnostic :: diagnostics))

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

let parserAt start end expression = ExprAt(spanFromBounds(start)(end))(expression)

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
            let first = Ashes.Number.UInt.toInt(Ashes.Byte.get(bytes)(0))
            in
                if first < 65
                then false
                else first <= 90)

let parserQualifiedName parts =
    match parts with
        | [] -> ("", "")
        | name :: moduleParts -> (join(".")(reverseList(moduleParts)), name)

let parserCurrentStartsNamedArgument (state: ParserState) =
    match state with
        | (first :: second :: _, _diagnostics) ->
            if first.kind == Ident
            then second.kind == Equals
            else false
        | _ -> false

let parserStateDiagnostics (state: ParserState) =
    match state with
        | (_tokens, diagnostics) -> diagnostics

let parserBuildCallArguments function arguments start end isWhitespace =
    (let recursive build current remaining =
        match remaining with
            | [] -> current
            | argument :: tail -> build(parserAt(start)(end)(ExprCall(current)(argument)(isWhitespace)))(tail)
    in build(function)(arguments))

let recursive parserParseExpression state = parserParsePipe(state)
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
                            | (right, afterRight) -> parserParsePipeTail(parserAt(parserExprStart(left))(parserExprEnd(right))(ExprCall(right)(left)(false)))(afterRight)
            | PipeQuestionGreater ->
                match parserAdvance(state) with
                    | (_operator, afterOperator) ->
                        match parserParseComparison(afterOperator) with
                            | (right, afterRight) -> parserParsePipeTail(parserAt(parserExprStart(left))(parserExprEnd(right))(ExprResultPipe(left)(right)))(afterRight)
            | PipeBangGreater ->
                match parserAdvance(state) with
                    | (_operator, afterOperator) ->
                        match parserParseComparison(afterOperator) with
                            | (right, afterRight) -> parserParsePipeTail(parserAt(parserExprStart(left))(parserExprEnd(right))(ExprResultMapErrorPipe(left)(right)))(afterRight)
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
                                    in parserParseComparisonTail(parserAt(parserExprStart(left))(parserExprEnd(right))(body))(afterRight))
and parserParseBitwiseOr state =
    match parserParseBitwiseXor(state) with
        | (left, nextState) -> parserParseBitwiseOrTail(left)(nextState)
and parserParseBitwiseOrTail left state =
    (let current = parserCurrent(state)
    in
        if current.kind != Pipe
        then (left, state)
        else
            match parserAdvance(state) with
                | (_operator, afterOperator) ->
                    match parserParseBitwiseXor(afterOperator) with
                        | (right, afterRight) -> parserParseBitwiseOrTail(parserAt(parserExprStart(left))(parserExprEnd(right))(ExprBitwiseOr(left)(right)))(afterRight))
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
                        | (right, afterRight) -> parserParseBitwiseXorTail(parserAt(parserExprStart(left))(parserExprEnd(right))(ExprBitwiseXor(left)(right)))(afterRight))
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
                        | (right, afterRight) -> parserParseBitwiseAndTail(parserAt(parserExprStart(left))(parserExprEnd(right))(ExprBitwiseAnd(left)(right)))(afterRight))
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
                                | (right, afterRight) -> (parserAt(parserExprStart(left))(parserExprEnd(right))(ExprCons(left)(right)), afterRight)
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
                    in parserParseShiftTail(parserAt(parserExprStart(left))(parserExprEnd(right))(body))(afterRight)
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
                    in parserParseAdditiveTail(parserAt(parserExprStart(left))(parserExprEnd(right))(body))(afterRight)
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
                    in parserParseMultiplicativeTail(parserAt(parserExprStart(left))(parserExprEnd(right))(body))(afterRight)
and parserParseUnary state =
    (let current = parserCurrent(state)
    in
        match current.kind with
            | Bang ->
                match parserAdvance(state) with
                    | (operator, afterOperator) ->
                        match parserParseUnary(afterOperator) with
                            | (operand, afterOperand) -> (parserAt(operator.position)(parserExprEnd(operand))(ExprLogicalNot(operand)), afterOperand)
            | Tilde ->
                match parserAdvance(state) with
                    | (operator, afterOperator) ->
                        match parserParseUnary(afterOperator) with
                            | (operand, afterOperand) -> (parserAt(operator.position)(parserExprEnd(operand))(ExprBitwiseNot(operand)), afterOperand)
            | Await ->
                match parserAdvance(state) with
                    | (operator, afterOperator) ->
                        match parserParseCall(afterOperator) with
                            | (operand, afterOperand) -> (parserAt(operator.position)(parserExprEnd(operand))(ExprAwait(operand)), afterOperand)
            | Perform ->
                match parserAdvance(state) with
                    | (operator, afterOperator) ->
                        match parserParseCall(afterOperator) with
                            | (operand, afterOperand) -> (parserAt(operator.position)(parserExprEnd(operand))(ExprPerform(operand)), afterOperand)
            | Minus ->
                match parserAdvance(state) with
                    | (operator, afterOperator) ->
                        match parserParseUnary(afterOperator) with
                            | (operand, afterOperand) ->
                                match parserUnspan(operand) with
                                    | ExprFloat(value, text) -> (parserAt(operator.position)(parserExprEnd(operand))(ExprFloat(0.0 - value)(parserToggleFloatSign(text))), afterOperand)
                                    | _ ->
                                        let zero = parserAt(operator.position)(operator.position + 1)(ExprInt(0))
                                        in (parserAt(operator.position)(parserExprEnd(operand))(ExprSubtract(zero)(operand)), afterOperand)
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
                    | (_leftParen, afterLeftParen) ->
                        if parserCurrentStartsNamedArgument(afterLeftParen)
                        then parserParseRecordArguments(function)(start)(afterLeftParen)
                        else
                            match parserParseCallArgumentList(afterLeftParen) with
                                | (arguments, rightParen, afterArguments) ->
                                    let applied = parserBuildCallArguments(function)(arguments)(start)(tokenEnd(rightParen))(false)
                                    in parserParseCallTail(applied)(afterArguments)
        else
            if parserIsWhitespaceArgument(current.kind)
            then
                let start = parserExprStart(function)
                in
                    match parserParseWhitespaceArgument(state) with
                        | (argument, afterArgument) -> parserParseCallTail(parserAt(start)(parserExprEnd(argument))(ExprCall(function)(argument)(true)))(afterArgument)
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
                        | (argument, afterArgument) -> parserParseMoreCallArguments(argument :: reversedArguments)(afterArgument)
        else
            match parserConsume(RParen)(state) with
                | (rightParen, afterRightParen) -> (reverseList(reversedArguments), rightParen, afterRightParen))
and parserParseRecordArguments function start state =
    match parserUnspan(function) with
        | ExprVar(typeName) ->
            let checkedState =
                if parserIsUpperName(typeName)
                then state
                else parserDiagnostic(state)(parserCurrent(state))("Named arguments are only allowed in record construction.")
            in
                match parserParseNamedFields([])(checkedState) with
                    | (fields, rightParen, afterFields) -> parserParseCallTail(parserAt(start)(tokenEnd(rightParen))(ExprRecord(typeName)(fields)))(afterFields)
        | _ ->
            let diagnosed = parserDiagnostic(state)(parserCurrent(state))("Named arguments are only allowed in record construction.")
            in
                match parserParseNamedFields([])(diagnosed) with
                    | (fields, rightParen, afterFields) -> parserParseCallTail(parserAt(start)(tokenEnd(rightParen))(ExprRecord("")(fields)))(afterFields)
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
                                        | (rightParen, afterRightParen) -> (reverseList(fields), rightParen, afterRightParen)
and parserParseWhitespaceArgument state =
    if parserCurrentKind(state) == Await
    then parserParseUnary(state)
    else parserParsePrimary(state)
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
                            then (parserAt(token.position)(tokenEnd(token))(ExprUInt(token.intValue)(bits)), afterToken)
                            else (parserAt(token.position)(tokenEnd(token))(ExprInt(token.intValue)), afterToken)
            | BigInt ->
                match parserAdvance(state) with
                    | (token, afterToken) -> (parserAt(token.position)(tokenEnd(token))(ExprBigInt(token.text)), afterToken)
            | Float ->
                match parserAdvance(state) with
                    | (token, afterToken) -> (parserAt(token.position)(tokenEnd(token))(ExprFloat(token.floatValue)(token.text)), afterToken)
            | String ->
                match parserAdvance(state) with
                    | (token, afterToken) -> (parserAt(token.position)(tokenEnd(token))(ExprString(token.text)), afterToken)
            | Rune ->
                match parserAdvance(state) with
                    | (token, afterToken) -> (parserAt(token.position)(tokenEnd(token))(ExprRune(token.intValue)), afterToken)
            | True ->
                match parserAdvance(state) with
                    | (token, afterToken) -> (parserAt(token.position)(tokenEnd(token))(ExprBool(true)), afterToken)
            | False ->
                match parserAdvance(state) with
                    | (token, afterToken) -> (parserAt(token.position)(tokenEnd(token))(ExprBool(false)), afterToken)
            | Ident -> parserParseVariable(state)
            | LParen -> parserParseParenthesized(state)
            | LBracket -> parserParseList(state)
            | _ -> parserBadPrimary(state))
and parserParseVariable state =
    match parserAdvance(state) with
        | (first, afterFirst) -> parserParseVariableTail(first.position)(tokenEnd(first))(first.text :: [])(afterFirst)
and parserParseVariableTail start end reversedParts state =
    (let current = parserCurrent(state)
    in
        if current.kind != Dot
        then
            match parserQualifiedName(reversedParts) with
                | (moduleName, name) ->
                    if moduleName == ""
                    then (parserAt(start)(end)(ExprVar(name)), state)
                    else (parserAt(start)(end)(ExprQualifiedVar(moduleName)(name)), state)
        else
            match parserAdvance(state) with
                | (_dot, afterDot) ->
                    match parserConsume(Ident)(afterDot) with
                        | (part, afterPart) -> parserParseVariableTail(start)(tokenEnd(part))(part.text :: reversedParts)(afterPart))
and parserParseParenthesized state =
    match parserAdvance(state) with
        | (leftParen, afterLeftParen) ->
            match parserParseExpression(afterLeftParen) with
                | (first, afterFirst) ->
                    if parserCurrentKind(afterFirst) == Comma
                    then parserParseTupleTail(leftParen.position)(first :: [])(afterFirst)
                    else
                        match parserConsume(RParen)(afterFirst) with
                            | (_rightParen, afterRightParen) -> (first, afterRightParen)
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
                                | (rightParen, afterRightParen) -> (parserAt(start)(tokenEnd(rightParen))(ExprTuple(reverseList(elements))), afterRightParen)
and parserParseList state =
    match parserAdvance(state) with
        | (leftBracket, afterLeftBracket) ->
            if parserCurrentKind(afterLeftBracket) == RBracket
            then
                match parserAdvance(afterLeftBracket) with
                    | (rightBracket, afterRightBracket) -> (parserAt(leftBracket.position)(tokenEnd(rightBracket))(ExprList([])), afterRightBracket)
            else
                match parserParseExpression(afterLeftBracket) with
                    | (first, afterFirst) -> parserParseListTail(leftBracket.position)(first :: [])(afterFirst)
and parserParseListTail start reversedElements state =
    (let current = parserCurrent(state)
    in
        if current.kind == Comma
        then
            match parserAdvance(state) with
                | (_comma, afterComma) ->
                    match parserParseExpression(afterComma) with
                        | (element, afterElement) -> parserParseListTail(start)(element :: reversedElements)(afterElement)
        else
            match parserConsume(RBracket)(state) with
                | (rightBracket, afterRightBracket) -> (parserAt(start)(tokenEnd(rightBracket))(ExprList(reverseList(reversedElements))), afterRightBracket))
and parserBadPrimary state =
    (let current = parserCurrent(state)
    in
        let diagnosed = parserDiagnostic(state)(current)("Expected expression but found " + tokenKindName(current.kind) + ".")
        in
            match parserAdvance(diagnosed) with
                | (_bad, afterBad) -> (parserAt(current.position)(tokenEnd(current))(ExprInt(0)), afterBad))

let parseExpression source =
    (let lexed = tokenize(source)
    in
        let initial : ParserState = (lexed.tokens, [])
        in
            match parserParseExpression(initial) with
                | (expression, state) ->
                    let current = parserCurrent(state)
                    in
                        let finalState =
                            if current.kind == EOF
                            then state
                            else parserDiagnostic(state)(current)("Unexpected token after end of expression: " + tokenKindName(current.kind) + ".")
                        in
                            let parserDiagnostics = reverseList(parserStateDiagnostics(finalState))
                            in ExpressionParseResult(expression = expression, diagnostics = appendList(lexed.diagnostics)(parserDiagnostics)))
