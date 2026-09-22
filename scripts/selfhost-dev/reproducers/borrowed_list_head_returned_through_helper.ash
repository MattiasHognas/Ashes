type Token =
    | text: Str
    | position: Int
    | length: Int

type Span =
    | start: Int
    | end: Int

type Entry =
    | span: Span
    | message: Str
    | code: Maybe(Str)

type LexResult =
    | entries: List(Entry)

let mkToken (text: Str) (position: Int) (length: Int) = Token(text = text, position = position, length = length)

let mkEntry (position: Int) (message: Str) = Entry(span = Span(start = position, end = position + 1), message = message, code = Some("ASH003"))

let recursive readChars (bytes: Bytes) (byteCount: Int) (position: Int) (value: Str) =
    if position >= byteCount
    then (value, position, false)
    else
        if Ashes.Number.UInt.toInt(Ashes.Byte.get(bytes)(position)) == 34
        then (value, position + 1, true)
        else readChars(bytes)(byteCount)(position + 1)(value + Ashes.Byte.subText(bytes)(position)(1))

let readString (bytes: Bytes) (byteCount: Int) (start: Int) =
    match readChars(bytes)(byteCount)(start + 1)("") with
        | (value, endPosition, terminated) ->
            if terminated
            then (mkToken(value)(start)(endPosition - start), None)
            else
                (mkToken(value)(start)(endPosition - start), "unterminated"
                |> mkEntry(start)
                |> Some)

let recursive scan (bytes: Bytes) (byteCount: Int) (position: Int) (entries: List(Entry)) =
    if position >= byteCount
    then Ashes.Collection.List.reverse(entries)
    else
        match readString(bytes)(byteCount)(position) with
            | (token, entry) ->
                let next =
                    match entry with
                        | None -> entries
                        | Some(value) -> value :: entries
                in scan(bytes)(byteCount)(token.position + token.length)(next)

let tokenize (source: Str) =
    (let bytes = Ashes.Byte.fromText(source)
    in
        LexResult(entries = scan(bytes)(Ashes.Byte.length(bytes))(0)([])))

let firstDiagnostic (source: Str) =
    (let result = tokenize(source)
    in
        match result.entries with
            | head :: _ -> head
            | [] -> Entry(span = Span(start = 0, end = 0), message = "none", code = None))

let check (source: Str) =
    source
    |> firstDiagnostic
    |> (given (d) -> d.message)

"\"abc"
|> check
|> (given (a) -> check("\"abcd") + a)
|> Ashes.IO.print
