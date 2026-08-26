// expect: 4 alpha, 5 beta, 1 stop of 21
// A reference-counted ADT returned by one function (classify) and handed to another that keeps it
// in its result (the curried makeTok builder) must survive the caller's own release of it. The
// callee takes ownership of the argument the same way a string parameter already does; without
// that, every token's kind read as whatever the freed cell was reused for.
type Kind =
    | Alpha
    | Beta
    | Gamma
    | Stop

type Tok =
    | kind: Kind
    | text: Str
    | intValue: Int
    | floatValue: Float
    | position: Int
    | length: Int

let makeTok (kind: Kind) (text: Str) (intValue: Int) (floatValue: Float) (position: Int) (length: Int) = Tok(kind = kind, text = text, intValue = intValue, floatValue = floatValue, position = position, length = length)

let classify (text: Str) =
    match text with
        | "abc" -> Alpha
        | "def" -> Beta
        | _ -> Gamma

let fixedTok (bytes: Bytes) (position: Int) =
    if Ashes.Number.UInt.toInt(Ashes.Byte.get(bytes)(position)) == 120
    then Some(makeTok(Beta)("x")(0)(0.0)(position)(1))
    else None

let readNext (bytes: Bytes) (position: Int) =
    match fixedTok(bytes)(position) with
        | Some(token) -> (token, None)
        | None ->
            let text = Ashes.Byte.subText(bytes)(position)(3)
            in
                let kind = classify(text)
                in (makeTok(kind)(text)(0)(0.0)(position)(3), Some("note"))

let recursive scan (bytes: Bytes) (byteCount: Int) (position: Int) toks notes =
    if position >= byteCount
    then (makeTok(Stop)("")(0)(0.0)(position)(0) :: toks, notes)
    else
        match readNext(bytes)(position) with
            | (token, note) ->
                let nextNotes =
                    match note with
                        | None -> notes
                        | Some(value) -> value :: notes
                in scan(bytes)(byteCount)(position + token.length)(token :: toks)(nextNotes)

let recursive countAlpha toks acc =
    match toks with
        | [] -> acc
        | t :: rest ->
            match t.kind with
                | Alpha -> countAlpha(rest)(acc + 1)
                | _ -> countAlpha(rest)(acc)

let recursive countBeta toks acc =
    match toks with
        | [] -> acc
        | t :: rest ->
            match t.kind with
                | Beta -> countBeta(rest)(acc + 1)
                | _ -> countBeta(rest)(acc)

let recursive countStop toks acc =
    match toks with
        | [] -> acc
        | t :: rest ->
            match t.kind with
                | Stop -> countStop(rest)(acc + 1)
                | _ -> countStop(rest)(acc)

let source = "abcdefabcxxxabcdefabc"

let bytes = Ashes.Byte.fromText(source)

match scan(bytes)(Ashes.Byte.length(bytes))(0)([])([]) with
    | (toks, _notes) -> Ashes.IO.print(Ashes.Text.fromInt(countAlpha(toks)(0)) + " alpha, " + Ashes.Text.fromInt(countBeta(toks)(0)) + " beta, " + Ashes.Text.fromInt(countStop(toks)(0)) + " stop of " + Ashes.Text.fromInt(Ashes.Text.byteLength(source)))
