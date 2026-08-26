// expect: 64000 563184303 0 | 64000 563184303 0 | 64000 563184303 0
// An owned reference-counted binding passed as an earlier argument of a curried call is captured by
// the stage closure and read by the final body. Its release must wait for the last application of
// the chain, not the stage that captured it; a 192 KB string leaves the arena, so an early release
// is a use-after-free. The string itself, a byte view bound with let, and a byte view taken at the
// call site all take that path; the last one is a borrowed read of the binding, not a fresh value,
// so it must not be released a second time as a consumed call argument either.
type Tok =
    | kind: Int
    | text: Str
    | position: Int

type Note =
    | span: Int
    | message: Str

let readNext (bytes: Bytes) (byteCount: Int) (position: Int) =
    (let width =
        if position + 3 <= byteCount
        then 3
        else byteCount - position
    in
        let text = Ashes.Byte.subText(bytes)(position)(width)
        in
            if width == 3
            then (Tok(kind = 1, text = text, position = position), None)
            else (Tok(kind = 2, text = text, position = position), Some(Note(span = position, message = "short: " + text))))

let recursive scan (bytes: Bytes) (byteCount: Int) (position: Int) toks notes =
    if position >= byteCount
    then (toks, notes)
    else
        match readNext(bytes)(byteCount)(position) with
            | (tok, note) ->
                let nextNotes =
                    match note with
                        | None -> notes
                        | Some(value) -> value :: notes
                in scan(bytes)(byteCount)(position + 3)(tok :: toks)(nextNotes)

let recursive scanText (text: Str) (byteCount: Int) (position: Int) toks notes =
    if position >= byteCount
    then (toks, notes)
    else
        match readNext(Ashes.Byte.fromText(text))(byteCount)(position) with
            | (tok, note) ->
                let nextNotes =
                    match note with
                        | None -> notes
                        | Some(value) -> value :: notes
                in scanText(text)(byteCount)(position + 3)(tok :: toks)(nextNotes)

let recursive checksum toks acc =
    match toks with
        | [] -> acc
        | t :: rest -> checksum(rest)((acc * 31 + Ashes.Text.byteLength(t.text) + t.position) % 1000000007)

let recursive countMatching toks acc =
    match toks with
        | [] -> acc
        | t :: rest ->
            if t.text == "abc"
            then countMatching(rest)(acc + 1)
            else countMatching(rest)(acc)

let recursive countNotes notes acc =
    match notes with
        | [] -> acc
        | _ :: rest -> countNotes(rest)(acc + 1)

let recursive grow s n =
    if n <= 0
    then s
    else grow(s + "abcabcabcabcabcabcabcabcabcabcabcabcabcabcabcabc")(n - 1)

let report scanned =
    match scanned with
        | (toks, notes) -> Ashes.Text.fromInt(countMatching(toks)(0)) + " " + Ashes.Text.fromInt(checksum(toks)(0)) + " " + Ashes.Text.fromInt(countNotes(notes)(0))

let viaView =
    (let source = grow("")(4000)
    in
        let bytes = Ashes.Byte.fromText(source)
        in report(scan(bytes)(Ashes.Byte.length(bytes))(0)([])([])))

let viaText =
    (let source = grow("")(4000)
    in report(scanText(source)(Ashes.Text.byteLength(source))(0)([])([])))

let viaCallSite =
    (let source = grow("")(4000)
    in
        let byteCount = Ashes.Text.byteLength(source)
        in report(scan(Ashes.Byte.fromText(source))(byteCount)(0)([])([])))

Ashes.IO.print(viaView + " | " + viaText + " | " + viaCallSite)
