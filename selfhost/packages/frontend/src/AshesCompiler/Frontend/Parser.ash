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

let parserCurrentText (state: ParserState) =
    (let current = parserCurrent(state)
    in current.text)

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
            let first = Ashes.Number.UInt.toInt(Ashes.Byte.get(bytes)(0))
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
            let first = Ashes.Number.UInt.toInt(Ashes.Byte.get(bytes)(0))
            in
                if first < 97
                then false
                else first <= 122)

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

let parserPatternAt start end pattern = PatternAt(spanFromBounds(start)(end))(pattern)

let parserUnspanPattern pattern =
    match pattern with
        | PatternAt(_span, inner) -> inner
        | _ -> pattern

let parserPipeStartsArm (state: ParserState) =
    (let recursive scan tokens parenthesisDepth bracketDepth =
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
            | (pipe :: tail, _diagnostics) ->
                if pipe.kind == Pipe
                then scan(tail)(0)(0)
                else false
            | _ -> false)

let parserLetStartsPattern (state: ParserState) =
    match state with
        | (letToken :: next :: tail, _diagnostics) ->
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

let parserBuildCallArguments function arguments start end isWhitespace =
    (let recursive build current remaining =
        match remaining with
            | [] -> current
            | argument :: tail -> build(parserAt(start)(end)(ExprCall(current)(argument)(isWhitespace)))(tail)
    in build(function)(arguments))

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
                                        | (firstCase, afterFirst) -> parserParseMatchCases(matchToken.position)(scrutinee)(firstCase :: [])(afterFirst)
and parserParseMatchCases start scrutinee reversedCases state =
    if parserCurrentKind(state) != Pipe
    then (parserAt(start)(parserLastMatchEnd(reversedCases)(scrutinee))(ExprMatch(scrutinee)(reverseList(reversedCases))(Some(start))), state)
    else
        match parserAdvance(state) with
            | (_pipe, afterPipe) ->
                match parserParseMatchCase(afterPipe) with
                    | (nextCase, afterCase) -> parserParseMatchCases(start)(scrutinee)(nextCase :: reversedCases)(afterCase)
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
                                        | (firstArm, afterFirst) -> parserParseHandlerArms(handleToken.position)(body)(firstArm :: [])(afterFirst)
and parserParseHandlerArms start body reversedArms state =
    if parserCurrentKind(state) != Pipe
    then
        match reversedArms with
            | (_capability, _operation, _parameters, lastBody) :: _ -> (parserAt(start)(parserExprEnd(lastBody))(ExprHandle(body)(reverseList(reversedArms))), state)
            | [] -> (parserAt(start)(parserExprEnd(body))(ExprHandle(body)([])), state)
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
                        let diagnosed = parserDiagnostic(afterHead)(head)("Handler arm must be 'Capability.op(args)' or 'return(value)'.")
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
                                                    let missing = parserAt(elseToken.position)(elseToken.position)(ExprInt(0))
                                                    in (parserAt(ifToken.position)(parserExprEnd(thenBranch))(ExprIf(condition)(thenBranch)(missing)), afterElse)
                                                else
                                                    match parserParseExpression(afterElse) with
                                                        | (elseBranch, afterElseBranch) -> (parserAt(ifToken.position)(parserExprEnd(elseBranch))(ExprIf(condition)(thenBranch)(elseBranch)), afterElseBranch)
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
                            match parserParseSugarParameters([])(afterName) with
                                | (parameters, afterParameters) ->
                                    match parserConsume(Equals)(afterParameters) with
                                        | (_equals, afterEquals) ->
                                            match parserParseExpression(afterEquals) with
                                                | (rawValue, afterValue) ->
                                                    match parserConsume(In)(afterValue) with
                                                        | (_inToken, afterIn) ->
                                                            match parserParseExpression(afterIn) with
                                                                | (body, afterBody) ->
                                                                    let value = parserBuildLambdas(parameters)(rawValue)(letToken.position)
                                                                    in
                                                                        let expression =
                                                                            if recursiveBinding
                                                                            then ExprLetRecursive(name.text)(value)(body)(parameters)(None)([])
                                                                            else ExprLet(name.text)(value)(body)(parameters)(None)([])
                                                                        in (parserAt(letToken.position)(parserExprEnd(body))(expression), afterBody)
and parserParseSugarParameters reversed state =
    if parserCurrentKind(state) != Ident
    then (reverseList(reversed), state)
    else
        match parserAdvance(state) with
            | (parameter, afterParameter) -> parserParseSugarParameters(parameter.text :: reversed)(afterParameter)
and parserBuildLambdas parameters body start =
    (let recursive build reversed current =
        match reversed with
            | [] -> current
            | parameter :: tail -> build(tail)(parserAt(start)(parserExprEnd(current))(ExprLambda(parameter)(current)(None)))
    in build(reverseList(parameters))(body))
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
                                                | (body, afterBody) -> (parserAt(letToken.position)(parserExprEnd(body))(ExprLetResult(name.text)(value)(body)), afterBody)
and parserParseLetBang state =
    match parserAdvance(state) with
        | (letToken, afterLet) ->
            match parserConsume(Ident)(afterLet) with
                | (name, afterName) ->
                    match parserConsume(Equals)(afterName) with
                        | (_equals, afterEquals) ->
                            match parserParseExpression(afterEquals) with
                                | (value, afterValue) ->
                                    let awaited = parserAt(parserExprStart(value))(parserExprEnd(value))(ExprAwait(value))
                                    in
                                        let bodyResult =
                                            if parserCurrentKind(afterValue) == LetBang
                                            then parserParseLetBang(afterValue)
                                            else
                                                match parserConsume(In)(afterValue) with
                                                    | (_inToken, afterIn) -> parserParseExpression(afterIn)
                                        in
                                            match bodyResult with
                                                | (body, afterBody) -> (parserAt(letToken.position)(parserExprEnd(body))(ExprLet(name.text)(awaited)(body)([])(None)([])), afterBody)
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
                            | (_leftParen, afterLeftParen) -> parserParseNameList(afterLeftParen)(RParen)
                    else
                        match parserConsume(Ident)(afterGiven) with
                            | (parameter, afterParameter) -> (parameter.text :: [], afterParameter)
                in
                    match parametersResult with
                        | (parameters, afterParameters) ->
                            match parserConsume(Arrow)(afterParameters) with
                                | (_arrow, afterArrow) ->
                                    match parserParseExpression(afterArrow) with
                                        | (body, afterBody) -> (parserBuildLambdas(parameters)(body)(givenToken.position), afterBody)
and parserParseNameList state terminator =
    match parserConsume(Ident)(state) with
        | (first, afterFirst) -> parserParseMoreNames(first.text :: [])(afterFirst)(terminator)
and parserParseMoreNames reversed state terminator =
    if parserCurrentKind(state) == Comma
    then
        match parserAdvance(state) with
            | (_comma, afterComma) ->
                match parserConsume(Ident)(afterComma) with
                    | (name, afterName) -> parserParseMoreNames(name.text :: reversed)(afterName)(terminator)
    else
        match parserConsume(terminator)(state) with
            | (_end, afterEnd) -> (reverseList(reversed), afterEnd)
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
                    | (fields, afterFields) -> parserParseWithTail(parserAt(parserExprStart(target))(parserLastFieldEnd(fields)(target))(ExprRecordUpdate(target)(fields)))(afterFields)
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
            if parserPipeStartsArm(state)
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
                        | last :: _ -> (parserPatternAt(parserPatternStart(first))(parserPatternEnd(last))(PatternOr(alternatives)), state)
                        | [] -> (first, state)
    else
        match parserAdvance(state) with
            | (_pipe, afterPipe) ->
                match parserParsePatternAs(afterPipe) with
                    | (alternative, afterAlternative) -> parserParsePatternOrTail(first)(alternative :: reversed)(afterAlternative)
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
                                        then parserDiagnostic(afterName)(name)("An as-pattern must bind a lower-case name other than '_'.")
                                        else
                                            if parserIsLowerName(name.text)
                                            then afterName
                                            else parserDiagnostic(afterName)(name)("An as-pattern must bind a lower-case name other than '_'.")
                                    in (parserPatternAt(parserPatternStart(inner))(tokenEnd(name))(PatternAs(inner)(name.text)), checkedState)
and parserParsePatternCons state =
    match parserParsePatternPrimary(state) with
        | (head, afterHead) ->
            if parserCurrentKind(afterHead) != ColonColon
            then (head, afterHead)
            else
                match parserAdvance(afterHead) with
                    | (_colonColon, afterOperator) ->
                        match parserParsePatternCons(afterOperator) with
                            | (tail, afterTail) -> (parserPatternAt(parserPatternStart(head))(parserPatternEnd(tail))(PatternCons(head)(tail)), afterTail)
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
                | (rightBracket, afterRight) -> (parserPatternAt(leftBracket.position)(tokenEnd(rightBracket))(PatternEmptyList), afterRight)
and parserParseIdentifierPattern state =
    match parserAdvance(state) with
        | (name, afterName) ->
            if name.text == "_"
            then (parserPatternAt(name.position)(tokenEnd(name))(PatternWildcard), afterName)
            else
                match parserCurrentKind(afterName) with
                    | LParen ->
                        match parserAdvance(afterName) with
                            | (_leftParen, afterLeftParen) ->
                                match parserParsePatternList(afterLeftParen)(RParen) with
                                    | (patterns, afterPatterns) ->
                                        let endPosition =
                                            match afterPatterns with
                                                | (next :: _, _diagnostics) -> next.position
                                                | _ -> tokenEnd(name)
                                        in (parserPatternAt(name.position)(endPosition)(PatternConstructor(name.text)(patterns)), afterPatterns)
                    | LBrace ->
                        match parserAdvance(afterName) with
                            | (_leftBrace, afterLeftBrace) ->
                                match parserParsePatternFields([])(afterLeftBrace) with
                                    | (fields, rightBrace, afterFields) -> (parserPatternAt(name.position)(tokenEnd(rightBrace))(PatternRecord(name.text)(fields)), afterFields)
                    | _ -> (parserPatternAt(name.position)(tokenEnd(name))(PatternVar(name.text)), afterName)
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
                                            | (rightBrace, afterRightBrace) -> (reverseList(fields), rightBrace, afterRightBrace)
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
                                | (rightParen, afterRightParen) -> (parserPatternAt(start)(tokenEnd(rightParen))(PatternTuple(reverseList(elements))), afterRightParen)
and parserParseIntegerPattern state =
    match parserAdvance(state) with
        | (token, afterToken) -> (parserPatternAt(token.position)(tokenEnd(token))(PatternInt(token.intValue)), afterToken)
and parserParseNegativeIntegerPattern state =
    match parserAdvance(state) with
        | (minus, afterMinus) ->
            if parserCurrentKind(afterMinus) != Int
            then parserBadPattern(afterMinus)
            else
                match parserAdvance(afterMinus) with
                    | (token, afterToken) -> (parserPatternAt(minus.position)(tokenEnd(token))(PatternInt(-token.intValue)), afterToken)
and parserParseStringPattern state =
    match parserAdvance(state) with
        | (token, afterToken) -> (parserPatternAt(token.position)(tokenEnd(token))(PatternString(token.text)), afterToken)
and parserParseRunePattern state =
    match parserAdvance(state) with
        | (token, afterToken) -> (parserPatternAt(token.position)(tokenEnd(token))(PatternRune(token.intValue)), afterToken)
and parserParseBooleanPattern state value =
    match parserAdvance(state) with
        | (token, afterToken) -> (parserPatternAt(token.position)(tokenEnd(token))(PatternBool(value)), afterToken)
and parserBadPattern state =
    (let current = parserCurrent(state)
    in
        let diagnosed = parserDiagnostic(state)(current)("Expected pattern but found " + tokenKindName(current.kind) + ".")
        in
            match parserAdvance(diagnosed) with
                | (_bad, afterBad) -> (parserPatternAt(current.position)(tokenEnd(current))(PatternWildcard), afterBad))
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
                        else parserDiagnostic(afterPattern)(parserCurrent(afterPattern))("Refutable pattern in let binding. Only irrefutable patterns (variable, wildcard, tuple, cons) are allowed — use 'match' for refutable patterns.")
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
                                                        in (parserAt(letToken.position)(parserExprEnd(body))(ExprMatch(value)(cases)(Some(letToken.position))), afterBody)
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
