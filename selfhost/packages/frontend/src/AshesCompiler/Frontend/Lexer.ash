// Tokenizes Ashes source while retaining exact spellings and source locations.
//
// Invariants:
// - Token positions, lengths, and diagnostic spans are UTF-8 byte counts.
// - Malformed input produces source-ordered diagnostics and scanning always advances.
// - Token text remains the original spelling needed by parsing and formatting.

import Ashes.Text.Regex.compile as compileRegex
import Ashes.Text.Regex.isMatch as regexIsMatch
import AshesCompiler.Frontend.Token
export (
    type LexerResult(..),
    value tokenize,
)

type LexerResult =
    | tokens: List(Token)
    | diagnostics: List(DiagnosticEntry)
    deriving {Eq, Show}

let lexerUnicodeLetter = compileRegex("^\\p{L}$")

let lexerUnicodeIdentifierCharacter = compileRegex("^[\\p{L}\\p{Nd}]$")

let lexerUnicodeWhiteSpace = compileRegex("^\\s$")

let recursive reverseLexerValues remaining result =
    match remaining with
        | [] -> result
        | head :: tail -> reverseLexerValues(tail)(head :: result)

let lexerByteAt (bytes: Bytes) (position: Int) =
    position
    |> Ashes.Byte.get(bytes)
    |> Ashes.Number.UInt.toInt

let lexerSlice (bytes: Bytes) (position: Int) (count: Int) = Ashes.Byte.subText(bytes)(position)(count)

let lexerStartsAt (bytes: Bytes) (byteCount: Int) (position: Int) (expected: Str) =
    (let expectedLength = Ashes.Text.byteLength(expected)
    in
        if position + expectedLength > byteCount
        then false
        else lexerSlice(bytes)(position)(expectedLength) == expected)

let lexerToken (kind: TokenKind) (text: Str) (intValue: Int) (floatValue: Float) (position: Int) (length: Int) = Token(kind = kind, text = text, intValue = intValue, floatValue = floatValue, position = position, length = length)

let lexerDiagnostic (position: Int) (length: Int) (message: Str) = DiagnosticEntry(span = spanFromStartLength(position)(length), message = message, code = Some("ASH003"))

let lexerIsAsciiLetter value =
    if value >= 65
    then
        if value <= 90
        then true
        else
            if value >= 97
            then value <= 122
            else false
    else false

let lexerIsAsciiDigit value =
    if value < 48
    then false
    else value <= 57

let lexerIsIdentifierStart value =
    if lexerIsAsciiLetter(value)
    then true
    else value == 95

let lexerIsIdentifierCharacter value =
    if lexerIsIdentifierStart(value)
    then true
    else lexerIsAsciiDigit(value)

let lexerIsWhiteSpace value =
    if value == 32
    then true
    else
        if value == 9
        then true
        else
            if value == 10
            then true
            else value == 13

let lexerSourceRune (bytes: Bytes) (byteCount: Int) (position: Int) =
    match byteCount - position
    |> lexerSlice(bytes)(position)
    |> Ashes.Text.uncons with
        | None -> ('�', "", 0)
        | Some((value, _tail)) ->
            let text = Ashes.Rune.toText(value)
            in (value, text, Ashes.Text.byteLength(text))

let lexerRegexMatches compiled value =
    match compiled with
        | Ok(regex) -> regexIsMatch(regex)(value)
        | Error(_) -> false

let lexerIdentifierWidth (bytes: Bytes) (byteCount: Int) (position: Int) =
    (let first = lexerByteAt(bytes)(position)
    in
        if first < 128
        then
            if lexerIsIdentifierCharacter(first)
            then 1
            else 0
        else
            match lexerSourceRune(bytes)(byteCount)(position) with
                | (_rune, text, width) ->
                    if lexerRegexMatches(lexerUnicodeIdentifierCharacter)(text)
                    then width
                    else 0)

let lexerIdentifierStartWidth (bytes: Bytes) (byteCount: Int) (position: Int) =
    (let first = lexerByteAt(bytes)(position)
    in
        if first < 128
        then
            if lexerIsIdentifierStart(first)
            then 1
            else 0
        else
            match lexerSourceRune(bytes)(byteCount)(position) with
                | (_rune, text, width) ->
                    if lexerRegexMatches(lexerUnicodeLetter)(text)
                    then width
                    else 0)

let lexerWhiteSpaceWidth (bytes: Bytes) (byteCount: Int) (position: Int) =
    (let first = lexerByteAt(bytes)(position)
    in
        if first < 128
        then
            if lexerIsWhiteSpace(first)
            then 1
            else 0
        else
            match lexerSourceRune(bytes)(byteCount)(position) with
                | (_rune, text, width) ->
                    if lexerRegexMatches(lexerUnicodeWhiteSpace)(text)
                    then width
                    else 0)

let recursive lexerSkipLine (bytes: Bytes) (byteCount: Int) (position: Int) =
    if position >= byteCount
    then position
    else
        if lexerByteAt(bytes)(position) == 10
        then position
        else lexerSkipLine(bytes)(byteCount)(position + 1)

let recursive lexerSkipTrivia (bytes: Bytes) (byteCount: Int) (position: Int) =
    if position >= byteCount
    then position
    else
        let whiteSpaceWidth = lexerWhiteSpaceWidth(bytes)(byteCount)(position)
        in
            if whiteSpaceWidth > 0
            then lexerSkipTrivia(bytes)(byteCount)(position + whiteSpaceWidth)
            else
                let current = lexerByteAt(bytes)(position)
                in
                    if current == 47
                    then
                        if lexerStartsAt(bytes)(byteCount)(position)("//")
                        then
                            position + 2
                            |> lexerSkipLine(bytes)(byteCount)
                            |> lexerSkipTrivia(bytes)(byteCount)
                        else position
                    else position

let recursive lexerHashRange (bytes: Bytes) (position: Int) (remaining: Int) (value: Int) =
    if remaining <= 0
    then value
    else lexerHashRange(bytes)(position + 1)(remaining - 1)(value * 131 + lexerByteAt(bytes)(position))

let recursive lexerRangeEquals (expectedBytes: Bytes) (sourceBytes: Bytes) (sourcePosition: Int) (position: Int) (count: Int) =
    if position >= count
    then true
    else
        if lexerByteAt(expectedBytes)(position) != lexerByteAt(sourceBytes)(sourcePosition + position)
        then false
        else lexerRangeEquals(expectedBytes)(sourceBytes)(sourcePosition)(position + 1)(count)

let lexerKeywordMatches (sourceCode: Int) (bytes: Bytes) (start: Int) (count: Int) (expected: Str) =
    (let expectedBytes = Ashes.Byte.fromText(expected)
    in
        if count != Ashes.Byte.length(expectedBytes)
        then false
        else
            if sourceCode != lexerHashRange(expectedBytes)(0)(count)(0)
            then false
            else lexerRangeEquals(expectedBytes)(bytes)(start)(0)(count))

let lexerKeywordKind (bytes: Bytes) (start: Int) (count: Int) =
    (let sourceCode = lexerHashRange(bytes)(start)(count)(0)
    in
        if lexerKeywordMatches(sourceCode)(bytes)(start)(count)("recursive")
        then Recursive
        else
            if lexerKeywordMatches(sourceCode)(bytes)(start)(count)("in")
            then In
            else
                if lexerKeywordMatches(sourceCode)(bytes)(start)(count)("and")
                then And
                else
                    if lexerKeywordMatches(sourceCode)(bytes)(start)(count)("if")
                    then If
                    else
                        if lexerKeywordMatches(sourceCode)(bytes)(start)(count)("then")
                        then Then
                        else
                            if lexerKeywordMatches(sourceCode)(bytes)(start)(count)("else")
                            then Else
                            else
                                if lexerKeywordMatches(sourceCode)(bytes)(start)(count)("match")
                                then Match
                                else
                                    if lexerKeywordMatches(sourceCode)(bytes)(start)(count)("with")
                                    then With
                                    else
                                        if lexerKeywordMatches(sourceCode)(bytes)(start)(count)("when")
                                        then When
                                        else
                                            if lexerKeywordMatches(sourceCode)(bytes)(start)(count)("given")
                                            then Given
                                            else
                                                if lexerKeywordMatches(sourceCode)(bytes)(start)(count)("true")
                                                then True
                                                else
                                                    if lexerKeywordMatches(sourceCode)(bytes)(start)(count)("false")
                                                    then False
                                                    else
                                                        if lexerKeywordMatches(sourceCode)(bytes)(start)(count)("type")
                                                        then Type
                                                        else
                                                            if lexerKeywordMatches(sourceCode)(bytes)(start)(count)("await")
                                                            then Await
                                                            else
                                                                if lexerKeywordMatches(sourceCode)(bytes)(start)(count)("external")
                                                                then External
                                                                else
                                                                    if lexerKeywordMatches(sourceCode)(bytes)(start)(count)("capability")
                                                                    then Capability
                                                                    else
                                                                        if lexerKeywordMatches(sourceCode)(bytes)(start)(count)("needs")
                                                                        then Needs
                                                                        else
                                                                            if lexerKeywordMatches(sourceCode)(bytes)(start)(count)("provide")
                                                                            then Provide
                                                                            else
                                                                                if lexerKeywordMatches(sourceCode)(bytes)(start)(count)("perform")
                                                                                then Perform
                                                                                else
                                                                                    if lexerKeywordMatches(sourceCode)(bytes)(start)(count)("handle")
                                                                                    then Handle
                                                                                    else
                                                                                        if lexerKeywordMatches(sourceCode)(bytes)(start)(count)("trait")
                                                                                        then Trait
                                                                                        else
                                                                                            if lexerKeywordMatches(sourceCode)(bytes)(start)(count)("implement")
                                                                                            then Implement
                                                                                            else
                                                                                                if lexerKeywordMatches(sourceCode)(bytes)(start)(count)("requires")
                                                                                                then Requires
                                                                                                else
                                                                                                    if lexerKeywordMatches(sourceCode)(bytes)(start)(count)("deriving")
                                                                                                    then Deriving
                                                                                                    else Ident)

let recursive lexerIdentifierEnd (bytes: Bytes) (byteCount: Int) (position: Int) =
    if position >= byteCount
    then position
    else
        let width = lexerIdentifierWidth(bytes)(byteCount)(position)
        in
            if width > 0
            then lexerIdentifierEnd(bytes)(byteCount)(position + width)
            else position

let lexerReadIdentifier (bytes: Bytes) (byteCount: Int) (start: Int) =
    (let endPosition = lexerIdentifierEnd(bytes)(byteCount)(start)
    in
        let tokenLength = endPosition - start
        in
            let sourceCode = lexerHashRange(bytes)(start)(tokenLength)(0)
            in
                if lexerKeywordMatches(sourceCode)(bytes)(start)(tokenLength)("let")
                then
                    if lexerStartsAt(bytes)(byteCount)(endPosition)("?")
                    then lexerToken(LetQuestion)("let?")(0)(0.0)(start)(tokenLength + 1)
                    else
                        if lexerStartsAt(bytes)(byteCount)(endPosition)("!")
                        then lexerToken(LetBang)("let!")(0)(0.0)(start)(tokenLength + 1)
                        else
                            lexerToken(Let)(lexerSlice(bytes)(start)(tokenLength))(0)(0.0)(start)(tokenLength)
                else
                    let kind = lexerKeywordKind(bytes)(start)(tokenLength)
                    in
                        lexerToken(kind)(lexerSlice(bytes)(start)(tokenLength))(0)(0.0)(start)(tokenLength))

let recursive lexerDigitsEnd (bytes: Bytes) (byteCount: Int) (position: Int) =
    if position >= byteCount
    then position
    else
        if position
        |> lexerByteAt(bytes)
        |> lexerIsAsciiDigit
        then lexerDigitsEnd(bytes)(byteCount)(position + 1)
        else position

let lexerReadInteger (bytes: Bytes) (byteCount: Int) (start: Int) =
    (let endPosition = lexerDigitsEnd(bytes)(byteCount)(start)
    in
        let text = lexerSlice(bytes)(start)(endPosition - start)
        in
            match Ashes.Text.parseInt(text) with
                | Ok(value) -> (lexerToken(Int)(text)(value)(0.0)(start)(endPosition - start), None)
                | Error(_) ->
                    (lexerToken(Int)(text)(0)(0.0)(start)(endPosition - start), "Invalid integer literal: " + text + "."
                    |> lexerDiagnostic(start)(endPosition - start)
                    |> Some))

let lexerUnsignedSuffix (bytes: Bytes) (byteCount: Int) (position: Int) =
    if position + 2 > byteCount
    then (0, 0)
    else
        if lexerByteAt(bytes)(position) != 117
        then (0, 0)
        else
            if lexerByteAt(bytes)(position + 1) == 56
            then (8, 2)
            else
                if position + 3 > byteCount
                then (0, 0)
                else
                    let second = lexerByteAt(bytes)(position + 1)
                    in
                        let third = lexerByteAt(bytes)(position + 2)
                        in
                            if second == 49
                            then
                                if third == 54
                                then (16, 3)
                                else (0, 0)
                            else
                                if second == 51
                                then
                                    if third == 50
                                    then (32, 3)
                                    else (0, 0)
                                else
                                    if second == 54
                                    then
                                        if third == 52
                                        then (64, 3)
                                        else (0, 0)
                                    else (0, 0)

let lexerUnsignedMaximum bits =
    match bits with
        | 8 -> 255N
        | 16 -> 65535N
        | 32 -> 4294967295N
        | 64 -> 18446744073709551615N
        | _ -> 0N

let lexerUnsignedToInt value =
    (let signed =
        if value > 9223372036854775807N
        then value - 18446744073709551616N
        else value
    in
        match Ashes.Number.BigInt.toInt(signed) with
            | Ok(result) -> result
            | Error(_) -> 0)

let lexerReadNumber (bytes: Bytes) (byteCount: Int) (start: Int) =
    (let digitsEnd = lexerDigitsEnd(bytes)(byteCount)(start)
    in
        let isFloat =
            if digitsEnd + 1 >= byteCount
            then false
            else
                if lexerByteAt(bytes)(digitsEnd) != 46
                then false
                else
                    digitsEnd + 1
                    |> lexerByteAt(bytes)
                    |> lexerIsAsciiDigit
        in
            if isFloat
            then
                let endPosition = lexerDigitsEnd(bytes)(byteCount)(digitsEnd + 1)
                in
                    let tokenLength = endPosition - start
                    in
                        match tokenLength
                        |> lexerSlice(bytes)(start)
                        |> Ashes.Text.parseFloat with
                            | Ok(value) ->
                                (lexerToken(Float)(lexerSlice(bytes)(start)(tokenLength))(0)(value)(start)(tokenLength), None)
                            | Error(_) ->
                                (lexerToken(Float)(lexerSlice(bytes)(start)(tokenLength))(0)(0.0)(start)(tokenLength), "Invalid float literal: " + lexerSlice(bytes)(start)(tokenLength) + "."
                                |> lexerDiagnostic(start)(tokenLength)
                                |> Some)
            else
                if digitsEnd < byteCount
                then
                    if lexerByteAt(bytes)(digitsEnd) == 78
                    then
                        (lexerToken(BigInt)(lexerSlice(bytes)(start)(digitsEnd - start))(0)(0.0)(start)(digitsEnd + 1 - start), None)
                    else
                        match lexerUnsignedSuffix(bytes)(byteCount)(digitsEnd) with
                            | (bits, suffixLength) ->
                                let boundary = digitsEnd + suffixLength
                                in
                                    let boundaryValid =
                                        if suffixLength <= 0
                                        then false
                                        else
                                            if boundary >= byteCount
                                            then true
                                            else lexerIdentifierWidth(bytes)(byteCount)(boundary) <= 0
                                    in
                                        if boundaryValid
                                        then
                                            let fullLength = boundary - start
                                            in
                                                match digitsEnd - start
                                                |> lexerSlice(bytes)(start)
                                                |> Ashes.Text.parseBigInt with
                                                    | Error(_) ->
                                                        (lexerToken(Int)(lexerSlice(bytes)(start)(fullLength))(0)(0.0)(start)(fullLength), "Invalid unsigned integer literal: " + lexerSlice(bytes)(start)(fullLength) + "."
                                                        |> lexerDiagnostic(start)(fullLength)
                                                        |> Some)
                                                    | Ok(value) ->
                                                        let valueForRange = Ashes.Internal.deepCopy(value)
                                                        in
                                                            if valueForRange > lexerUnsignedMaximum(bits)
                                                            then
                                                                (lexerToken(Int)(lexerSlice(bytes)(start)(fullLength))(0)(0.0)(start)(fullLength), "Unsigned integer literal out of range for u" + Ashes.Text.fromInt(bits) + ": " + lexerSlice(bytes)(start)(fullLength) + "."
                                                                |> lexerDiagnostic(start)(fullLength)
                                                                |> Some)
                                                            else
                                                                (lexerToken(Int)(lexerSlice(bytes)(start)(fullLength))(lexerUnsignedToInt(value))(0.0)(start)(fullLength), None)
                                        else lexerReadInteger(bytes)(byteCount)(start)
                else lexerReadInteger(bytes)(byteCount)(start))

let recursive lexerReadStringChars (bytes: Bytes) (byteCount: Int) (position: Int) value =
    if position >= byteCount
    then (value, position, false)
    else
        let current = lexerByteAt(bytes)(position)
        in
            if current == 34
            then (value, position + 1, true)
            else
                if current == 92
                then
                    if position + 1 >= byteCount
                    then lexerReadStringChars(bytes)(byteCount)(position + 1)(value + "\\")
                    else
                        let escaped = lexerByteAt(bytes)(position + 1)
                        in
                            let decoded =
                                match escaped with
                                    | 110 -> "\n"
                                    | 114 -> "\r"
                                    | 116 -> "\t"
                                    | 34 -> "\""
                                    | 92 -> "\\"
                                    | _ -> lexerSlice(bytes)(position + 1)(1)
                            in lexerReadStringChars(bytes)(byteCount)(position + 2)(value + decoded)
                else lexerReadStringChars(bytes)(byteCount)(position + 1)(value + lexerSlice(bytes)(position)(1))

let lexerReadString (bytes: Bytes) (byteCount: Int) (start: Int) =
    match lexerReadStringChars(bytes)(byteCount)(start + 1)("") with
        | (value, endPosition, terminated) ->
            if terminated
            then (lexerToken(String)(value)(0)(0.0)(start)(endPosition - start), None)
            else
                (lexerToken(String)(value)(0)(0.0)(start)(endPosition - start), "Unterminated string literal."
                |> lexerDiagnostic(start)(endPosition - start)
                |> Some)

let lexerHexValue value =
    if value >= 48
    then
        if value <= 57
        then value - 48
        else
            if value >= 65
            then
                if value <= 70
                then value - 55
                else
                    if value >= 97
                    then
                        if value <= 102
                        then value - 87
                        else -1
                    else -1
            else -1
    else -1

let recursive lexerReadHex (bytes: Bytes) (byteCount: Int) (position: Int) digits value =
    if position >= byteCount
    then (value, position, digits, false)
    else
        if digits >= 6
        then (value, position, digits, lexerByteAt(bytes)(position) == 125)
        else
            let digit =
                position
                |> lexerByteAt(bytes)
                |> lexerHexValue
            in
                if digit < 0
                then (value, position, digits, lexerByteAt(bytes)(position) == 125)
                else lexerReadHex(bytes)(byteCount)(position + 1)(digits + 1)(value * 16 + digit)

let lexerValidScalar value =
    if value < 0
    then false
    else
        if value > 1114111
        then false
        else
            if value >= 55296
            then value > 57343
            else true

let recursive lexerFindRuneEnd (bytes: Bytes) (byteCount: Int) position =
    if position >= byteCount
    then position
    else
        let current = lexerByteAt(bytes)(position)
        in
            if current == 39
            then position + 1
            else
                if current == 10
                then position
                else
                    if current == 13
                    then position
                    else lexerFindRuneEnd(bytes)(byteCount)(position + 1)

let lexerShortEscape value =
    match value with
        | 92 -> Some(92)
        | 39 -> Some(39)
        | 110 -> Some(10)
        | 114 -> Some(13)
        | 116 -> Some(9)
        | 48 -> Some(0)
        | _ -> None

let lexerReadRune (bytes: Bytes) (byteCount: Int) (start: Int) =
    (let contentStart = start + 1
    in
        let parsed =
            if contentStart >= byteCount
            then (65533, contentStart, false)
            else
                if lexerByteAt(bytes)(contentStart) == 92
                then
                    if contentStart + 1 >= byteCount
                    then (65533, contentStart + 1, false)
                    else
                        let escape = lexerByteAt(bytes)(contentStart + 1)
                        in
                            if escape == 117
                            then
                                if contentStart + 2 < byteCount
                                then
                                    if lexerByteAt(bytes)(contentStart + 2) == 123
                                    then
                                        match lexerReadHex(bytes)(byteCount)(contentStart + 3)(0)(0) with
                                            | (value, bracePosition, digits, validHex) ->
                                                let closingPosition = bracePosition + 1
                                                in
                                                    let valid =
                                                        if !validHex
                                                        then false
                                                        else
                                                            if digits <= 0
                                                            then false
                                                            else
                                                                if !lexerValidScalar(value)
                                                                then false
                                                                else
                                                                    if closingPosition >= byteCount
                                                                    then false
                                                                    else lexerByteAt(bytes)(closingPosition) == 39
                                                    in
                                                        if valid
                                                        then (value, closingPosition + 1, true)
                                                        else (65533, lexerFindRuneEnd(bytes)(byteCount)(contentStart), false)
                                    else (65533, lexerFindRuneEnd(bytes)(byteCount)(contentStart), false)
                                else (65533, lexerFindRuneEnd(bytes)(byteCount)(contentStart), false)
                            else
                                match lexerShortEscape(escape) with
                                    | None -> (65533, lexerFindRuneEnd(bytes)(byteCount)(contentStart), false)
                                    | Some(value) ->
                                        let closingPosition = contentStart + 2
                                        in
                                            if closingPosition < byteCount
                                            then
                                                if lexerByteAt(bytes)(closingPosition) == 39
                                                then (value, closingPosition + 1, true)
                                                else (65533, lexerFindRuneEnd(bytes)(byteCount)(contentStart), false)
                                            else (65533, closingPosition, false)
                else
                    let remaining = lexerSlice(bytes)(contentStart)(byteCount - contentStart)
                    in
                        match Ashes.Text.uncons(remaining) with
                            | None -> (65533, contentStart, false)
                            | Some((rune, _tail)) ->
                                let width =
                                    rune
                                    |> Ashes.Rune.toText
                                    |> Ashes.Text.byteLength
                                in
                                    let closingPosition = contentStart + width
                                    in
                                        if closingPosition < byteCount
                                        then
                                            if lexerByteAt(bytes)(closingPosition) == 39
                                            then (Ashes.Rune.toInt(rune), closingPosition + 1, true)
                                            else (65533, lexerFindRuneEnd(bytes)(byteCount)(contentStart), false)
                                        else (65533, closingPosition, false)
        in
            match parsed with
                | (value, endPosition, valid) ->
                    let token =
                        lexerToken(Rune)(lexerSlice(bytes)(start)(endPosition - start))(value)(0.0)(start)(endPosition - start)
                    in
                        if valid
                        then (token, None)
                        else
                            (token, "A rune literal must contain exactly one valid Unicode scalar value."
                            |> lexerDiagnostic(start)(endPosition - start)
                            |> Some))

let lexerFixedToken (bytes: Bytes) (byteCount: Int) (position: Int) =
    if lexerStartsAt(bytes)(byteCount)(position)("|?>")
    then
        3
        |> lexerToken(PipeQuestionGreater)("|?>")(0)(0.0)(position)
        |> Some
    else
        if lexerStartsAt(bytes)(byteCount)(position)("|!>")
        then
            3
            |> lexerToken(PipeBangGreater)("|!>")(0)(0.0)(position)
            |> Some
        else
            if lexerStartsAt(bytes)(byteCount)(position)("->")
            then
                2
                |> lexerToken(Arrow)("->")(0)(0.0)(position)
                |> Some
            else
                if lexerStartsAt(bytes)(byteCount)(position)(">=")
                then
                    2
                    |> lexerToken(GreaterEquals)(">=")(0)(0.0)(position)
                    |> Some
                else
                    if lexerStartsAt(bytes)(byteCount)(position)("<=")
                    then
                        2
                        |> lexerToken(LessEquals)("<=")(0)(0.0)(position)
                        |> Some
                    else
                        if lexerStartsAt(bytes)(byteCount)(position)("==")
                        then
                            2
                            |> lexerToken(EqualsEquals)("==")(0)(0.0)(position)
                            |> Some
                        else
                            if lexerStartsAt(bytes)(byteCount)(position)("!=")
                            then
                                2
                                |> lexerToken(BangEquals)("!=")(0)(0.0)(position)
                                |> Some
                            else
                                if lexerStartsAt(bytes)(byteCount)(position)("<<")
                                then
                                    2
                                    |> lexerToken(LessLess)("<<")(0)(0.0)(position)
                                    |> Some
                                else
                                    if lexerStartsAt(bytes)(byteCount)(position)(">>")
                                    then
                                        2
                                        |> lexerToken(GreaterGreater)(">>")(0)(0.0)(position)
                                        |> Some
                                    else
                                        if lexerStartsAt(bytes)(byteCount)(position)("::")
                                        then
                                            2
                                            |> lexerToken(ColonColon)("::")(0)(0.0)(position)
                                            |> Some
                                        else
                                            if lexerStartsAt(bytes)(byteCount)(position)("|>")
                                            then
                                                2
                                                |> lexerToken(PipeGreater)("|>")(0)(0.0)(position)
                                                |> Some
                                            else
                                                let value = lexerByteAt(bytes)(position)
                                                in
                                                    match value with
                                                        | 60 ->
                                                            1
                                                            |> lexerToken(LessThan)("<")(0)(0.0)(position)
                                                            |> Some
                                                        | 62 ->
                                                            1
                                                            |> lexerToken(GreaterThan)(">")(0)(0.0)(position)
                                                            |> Some
                                                        | 43 ->
                                                            1
                                                            |> lexerToken(Plus)("+")(0)(0.0)(position)
                                                            |> Some
                                                        | 45 ->
                                                            1
                                                            |> lexerToken(Minus)("-")(0)(0.0)(position)
                                                            |> Some
                                                        | 42 ->
                                                            1
                                                            |> lexerToken(Star)("*")(0)(0.0)(position)
                                                            |> Some
                                                        | 47 ->
                                                            1
                                                            |> lexerToken(Slash)("/")(0)(0.0)(position)
                                                            |> Some
                                                        | 37 ->
                                                            1
                                                            |> lexerToken(Percent)("%")(0)(0.0)(position)
                                                            |> Some
                                                        | 126 ->
                                                            1
                                                            |> lexerToken(Tilde)("~")(0)(0.0)(position)
                                                            |> Some
                                                        | 38 ->
                                                            1
                                                            |> lexerToken(Ampersand)("&")(0)(0.0)(position)
                                                            |> Some
                                                        | 94 ->
                                                            1
                                                            |> lexerToken(Caret)("^")(0)(0.0)(position)
                                                            |> Some
                                                        | 33 ->
                                                            1
                                                            |> lexerToken(Bang)("!")(0)(0.0)(position)
                                                            |> Some
                                                        | 61 ->
                                                            1
                                                            |> lexerToken(Equals)("=")(0)(0.0)(position)
                                                            |> Some
                                                        | 44 ->
                                                            1
                                                            |> lexerToken(Comma)(",")(0)(0.0)(position)
                                                            |> Some
                                                        | 124 ->
                                                            1
                                                            |> lexerToken(Pipe)("|")(0)(0.0)(position)
                                                            |> Some
                                                        | 40 ->
                                                            1
                                                            |> lexerToken(LParen)("(")(0)(0.0)(position)
                                                            |> Some
                                                        | 41 ->
                                                            1
                                                            |> lexerToken(RParen)(")")(0)(0.0)(position)
                                                            |> Some
                                                        | 91 ->
                                                            1
                                                            |> lexerToken(LBracket)("[")(0)(0.0)(position)
                                                            |> Some
                                                        | 93 ->
                                                            1
                                                            |> lexerToken(RBracket)("]")(0)(0.0)(position)
                                                            |> Some
                                                        | 46 ->
                                                            1
                                                            |> lexerToken(Dot)(".")(0)(0.0)(position)
                                                            |> Some
                                                        | 58 ->
                                                            1
                                                            |> lexerToken(Colon)(":")(0)(0.0)(position)
                                                            |> Some
                                                        | 123 ->
                                                            1
                                                            |> lexerToken(LBrace)("{")(0)(0.0)(position)
                                                            |> Some
                                                        | 125 ->
                                                            1
                                                            |> lexerToken(RBrace)("}")(0)(0.0)(position)
                                                            |> Some
                                                        | _ -> None

let lexerReadNext (bytes: Bytes) (byteCount: Int) (position: Int) =
    match lexerFixedToken(bytes)(byteCount)(position) with
        | Some(token) -> (token, None)
        | None ->
            let value = lexerByteAt(bytes)(position)
            in
                if value == 34
                then lexerReadString(bytes)(byteCount)(position)
                else
                    if value == 39
                    then lexerReadRune(bytes)(byteCount)(position)
                    else
                        if lexerIdentifierStartWidth(bytes)(byteCount)(position) > 0
                        then (lexerReadIdentifier(bytes)(byteCount)(position), None)
                        else
                            if lexerIsAsciiDigit(value)
                            then lexerReadNumber(bytes)(byteCount)(position)
                            else
                                let badWidth =
                                    if value < 128
                                    then 1
                                    else
                                        match lexerSourceRune(bytes)(byteCount)(position) with
                                            | (_rune, _text, width) -> width
                                in
                                    let text = lexerSlice(bytes)(position)(badWidth)
                                    in
                                        (lexerToken(Bad)(text)(0)(0.0)(position)(badWidth), "Unexpected character: '" + text + "'."
                                        |> lexerDiagnostic(position)(badWidth)
                                        |> Some)

let recursive lexerScan (bytes: Bytes) (byteCount: Int) (position: Int) (tokens: List(Token)) (diagnostics: List(DiagnosticEntry)) =
    (let nextPosition = lexerSkipTrivia(bytes)(byteCount)(position)
    in
        if nextPosition >= byteCount
        then LexerResult(tokens = reverseLexerValues(lexerToken(EOF)("")(0)(0.0)(nextPosition)(0) :: tokens)([]), diagnostics = reverseLexerValues(diagnostics)([]))
        else
            match lexerReadNext(bytes)(byteCount)(nextPosition) with
                | (token, nextDiagnostic) ->
                    let publishedToken = Ashes.Internal.deepCopy(token)
                    in
                        let nextDiagnostics =
                            match nextDiagnostic with
                                | None -> diagnostics
                                | Some(value) -> value :: diagnostics
                        in
                            lexerScan(bytes)(byteCount)(tokenEnd(token))(publishedToken :: tokens)(nextDiagnostics))

let tokenize source =
    (let bytes = Ashes.Byte.fromText(source)
    in
        lexerScan(bytes)(Ashes.Byte.length(bytes))(0)([])([]))
