// expect: 100|190
// A record built in an `if` arm from a `let`-bound string, inside a tuple the function returns: a
// let-bound name is not a borrowed string a reference-counted parent copies, so the record stays
// in the arena with the tuple and the result copy is the only reference-counted copy. Built on
// the reference-counted heap inside an arena tuple, the record and its string were never
// released.
type Tok =
    | kind: Int
    | text: Str

let readNext (position: Int) =
    (let text = Ashes.Text.fromInt(position)
    in
        if position < 1000
        then (Tok(kind = 1, text = text), None)
        else (Tok(kind = 2, text = text), Some(position)))

let recursive scan (count: Int) (position: Int) (kinds: Int) (bytes: Int) =
    if position >= count
    then Ashes.Text.fromInt(kinds) + "|" + Ashes.Text.fromInt(bytes)
    else
        match readNext(position) with
            | (Tok { kind = kind, text = text }, _note) -> scan(count)(position + 1)(kinds + kind)(bytes + Ashes.Text.byteLength(text))

0
|> scan(100)(0)(0)
|> Ashes.IO.print
