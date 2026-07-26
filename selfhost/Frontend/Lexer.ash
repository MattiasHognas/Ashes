import Tokens
import Ashes.Byte as bytes
import Ashes.Text as text
import Ashes.Collection.List as list
let charAt data pos = bytes.subText(data)(pos)(1)

let isCharAt data len pos ch =
    if pos < len
    then charAt(data)(pos) == ch
    else false

let tryLiteral data len pos lit litLen kind =
    if pos + litLen <= len
    then
        if bytes.subText(data)(pos)(litLen) == lit
        then Some(Token(kind = kind, text = lit, intValue = 0, floatValue = 0.0, position = pos, length = litLen))
        else None
    else None

let recursive tryFromTable data len pos table =
    match table with
        | [] -> None
        | pair :: rest ->
            match pair with
                | (lit, kind) ->
                    match tryLiteral(data)(len)(pos)(lit)(text.length(lit))(kind) with
                        | Some(t) -> Some(t)
                        | None -> tryFromTable(data)(len)(pos)(rest)

let multiCharTable = [("|?>", TokPipeQuestionGreater), ("|!>", TokPipeBangGreater), ("->", TokArrow), (">=", TokGreaterEquals), ("<=", TokLessEquals), ("==", TokEqualsEquals), ("!=", TokBangEquals), ("<<", TokLessLess), (">>", TokGreaterGreater), ("::", TokColonColon), ("|>", TokPipeGreater), ("<", TokLessThan), (">", TokGreaterThan)]

let singleCharTable = [("+", TokPlus), ("-", TokMinus), ("*", TokStar), ("/", TokSlash), ("%", TokPercent), ("~", TokTilde), ("&", TokAmpersand), ("^", TokCaret), ("=", TokEquals), (",", TokComma), ("|", TokPipe), ("(", TokLParen), (")", TokRParen), ("[", TokLBracket), ("]", TokRBracket), (".", TokDot), (":", TokColon), ("{", TokLBrace), ("}", TokRBrace)]

let tryMultiChar data len pos = tryFromTable(data)(len)(pos)(multiCharTable)

let trySingleChar data len pos = tryFromTable(data)(len)(pos)(singleCharTable)

let recursive skipToLineEnd data len pos =
    if pos >= len
    then pos
    else
        if charAt(data)(pos) == "\n"
        then pos
        else skipToLineEnd(data)(len)(pos + 1)

let recursive skipWhite data len pos =
    if pos >= len
    then pos
    else
        let c = charAt(data)(pos)
        in
            if text.isWhiteSpace(c)
            then skipWhite(data)(len)(pos + 1)
            else
                if c == "/"
                then
                    if isCharAt(data)(len)(pos + 1)("/")
                    then
                        pos + 2
                        |> skipToLineEnd(data)(len)
                        |> skipWhite(data)(len)
                    else pos
                else pos

let recursive readStringChars data len pos acc =
    if pos >= len
    then (acc, pos)
    else
        let c = charAt(data)(pos)
        in
            if c == "\""
            then (acc, pos + 1)
            else
                if c == "\\"
                then
                    if pos + 1 < len
                    then
                        let e = charAt(data)(pos + 1)
                        in
                            let decoded =
                                if e == "n"
                                then "\n"
                                else
                                    if e == "r"
                                    then "\r"
                                    else
                                        if e == "t"
                                        then "\t"
                                        else
                                            if e == "\""
                                            then "\""
                                            else
                                                if e == "\\"
                                                then "\\"
                                                else e
                            in readStringChars(data)(len)(pos + 2)(acc + decoded)
                    else readStringChars(data)(len)(pos + 1)(acc + "\\")
                else readStringChars(data)(len)(pos + 1)(acc + c)

let readString data len start =
    match readStringChars(data)(len)(start + 1)("") with
        | (content, endPos) -> Token(kind = TokString, text = content, intValue = 0, floatValue = 0.0, position = start, length = endPos - start)

let recursive digitRun data len pos =
    if pos >= len
    then pos
    else
        if pos
        |> charAt(data)
        |> text.isDigit
        then digitRun(data)(len)(pos + 1)
        else pos

let isFloatContinuation data len pos =
    if pos + 1 < len
    then
        if charAt(data)(pos) == "."
        then
            pos + 1
            |> charAt(data)
            |> text.isDigit
        else false
    else false

let readFloatNumber data len start afterDigits =
    (let afterFracDigits = digitRun(data)(len)(afterDigits + 1)
    in
        let floatText = bytes.subText(data)(start)(afterFracDigits - start)
        in
            let floatValue =
                match text.parseFloat(floatText) with
                    | Ok(v) -> v
                    | Error(_) -> 0.0
            in Token(kind = TokFloat, text = floatText, intValue = 0, floatValue = floatValue, position = start, length = afterFracDigits - start))

let suffixBoundaryOk data len pos =
    if pos >= len
    then true
    else
        let c = charAt(data)(pos)
        in
            if text.isLetter(c)
            then false
            else
                if text.isDigit(c)
                then false
                else
                    if c == "_"
                    then false
                    else true

let tryMatchSuffix data len pos suffix =
    (let suffixLen = text.length(suffix)
    in
        if pos + suffixLen > len
        then false
        else
            if bytes.subText(data)(pos)(suffixLen) == suffix
            then suffixBoundaryOk(data)(len)(pos + suffixLen)
            else false)

let readUnsignedSuffixBits data len pos =
    if pos + 1 >= len
    then 0
    else
        if charAt(data)(pos) == "u"
        then
            if tryMatchSuffix(data)(len)(pos)("u8")
            then 8
            else
                if tryMatchSuffix(data)(len)(pos)("u16")
                then 16
                else
                    if tryMatchSuffix(data)(len)(pos)("u32")
                    then 32
                    else
                        if tryMatchSuffix(data)(len)(pos)("u64")
                        then 64
                        else 0
        else 0

let unsignedSuffixLength bits =
    match bits with
        | 8 -> 2
        | 16 -> 3
        | 32 -> 3
        | 64 -> 3
        | _ -> 0

let parseIntOrZero numberText =
    match text.parseInt(numberText) with
        | Ok(v) -> v
        | Error(_) -> 0

let readIntegerNumber data len start digitsEnd =
    (let numberText = bytes.subText(data)(start)(digitsEnd - start)
    in
        if isCharAt(data)(len)(digitsEnd)("N")
        then Token(kind = TokBigInt, text = numberText, intValue = 0, floatValue = 0.0, position = start, length = digitsEnd + 1 - start)
        else
            let bits = readUnsignedSuffixBits(data)(len)(digitsEnd)
            in
                if bits > 0
                then
                    let suffixLen = unsignedSuffixLength(bits)
                    in
                        let fullLen = digitsEnd + suffixLen - start
                        in
                            let fullText = bytes.subText(data)(start)(fullLen)
                            in Token(kind = TokInt, text = fullText, intValue = parseIntOrZero(numberText), floatValue = 0.0, position = start, length = fullLen)
                else Token(kind = TokInt, text = numberText, intValue = parseIntOrZero(numberText), floatValue = 0.0, position = start, length = digitsEnd - start))

let readNumber data len start =
    (let afterDigits = digitRun(data)(len)(start)
    in
        if isFloatContinuation(data)(len)(afterDigits)
        then readFloatNumber(data)(len)(start)(afterDigits)
        else readIntegerNumber(data)(len)(start)(afterDigits))

let isIdentStart c =
    if text.isLetter(c)
    then true
    else c == "_"

let isIdentChar c =
    if text.isLetter(c)
    then true
    else
        if text.isDigit(c)
        then true
        else c == "_"

let recursive identRun data len pos =
    if pos >= len
    then pos
    else
        if pos
        |> charAt(data)
        |> isIdentChar
        then identRun(data)(len)(pos + 1)
        else pos

let readIdentifierOrKeyword data len start =
    (let afterIdent = identRun(data)(len)(start)
    in
        let word = bytes.subText(data)(start)(afterIdent - start)
        in
            if word == "let"
            then
                if isCharAt(data)(len)(afterIdent)("?")
                then Token(kind = TokLetQuestion, text = "let?", intValue = 0, floatValue = 0.0, position = start, length = afterIdent + 1 - start)
                else
                    if isCharAt(data)(len)(afterIdent)("!")
                    then Token(kind = TokLetBang, text = "let!", intValue = 0, floatValue = 0.0, position = start, length = afterIdent + 1 - start)
                    else Token(kind = keywordKind(word), text = word, intValue = 0, floatValue = 0.0, position = start, length = afterIdent - start)
            else Token(kind = keywordKind(word), text = word, intValue = 0, floatValue = 0.0, position = start, length = afterIdent - start))

let nextToken data len pos =
    (let c = charAt(data)(pos)
    in
        match tryMultiChar(data)(len)(pos) with
            | Some(t) -> t
            | None ->
                match trySingleChar(data)(len)(pos) with
                    | Some(t) -> t
                    | None ->
                        if c == "\""
                        then readString(data)(len)(pos)
                        else
                            if text.isDigit(c)
                            then readNumber(data)(len)(pos)
                            else
                                if isIdentStart(c)
                                then readIdentifierOrKeyword(data)(len)(pos)
                                else Token(kind = TokBad, text = c, intValue = 0, floatValue = 0.0, position = pos, length = 1))

let recursive scanTokens data len pos acc =
    (let pos2 = skipWhite(data)(len)(pos)
    in
        if pos2 >= len
        then Token(kind = TokEof, text = "", intValue = 0, floatValue = 0.0, position = pos2, length = 0) :: acc
        else
            let tok = nextToken(data)(len)(pos2)
            in scanTokens(data)(len)(pos2 + tok.length)(tok :: acc))

let tokenize source =
    (let data = bytes.fromText(source)
    in
        let len = bytes.length(data)
        in
            []
            |> scanTokens(data)(len)(0)
            |> list.reverse)
