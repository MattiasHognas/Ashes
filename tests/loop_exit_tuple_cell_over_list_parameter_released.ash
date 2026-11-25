// expect: 6 alpha, 3 beta, 1 stop of 21
// A loop's exit returns a tuple whose first part is a cell consed onto the loop's list parameter.
// The cell stays in the arena and retains the parameter's list; the tuple result is copied onto
// the reference-counted heap, and the cell's retain is released behind it.
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
    then
        1
        |> makeTok(Beta)("x")(0)(0.0)(position)
        |> Some
    else None

let readNext (bytes: Bytes) (position: Int) =
    match fixedTok(bytes)(position) with
        | Some(token) -> (token, None)
        | None ->
            let text = Ashes.Byte.subText(bytes)(position)(3)
            in
                let kind = Alpha
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
    | (toks, _notes) ->
        Ashes.IO.print(Ashes.Text.fromInt(countAlpha(toks)(0)) + " alpha, " + Ashes.Text.fromInt(countBeta(toks)(0)) + " beta, " + Ashes.Text.fromInt(countStop(toks)(0)) + " stop of " + Ashes.Text.fromInt(Ashes.Text.byteLength(source)))
