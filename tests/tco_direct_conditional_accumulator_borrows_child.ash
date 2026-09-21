// A conditional accumulator written in place at the self-call, already placed on the
// reference-counted heap: the arena cell built for it borrows the record it conses, since the
// back edge copies the successor with references of its own.
// expect: 882909
type Tok =
    | kind: Int
    | text: Str
    | position: Int

type Diag =
    | message: Str
    | at: Int

let readNext (position: Int) = (Tok(kind = 2, text = Ashes.Text.fromInt(position), position = position), Diag(message = "odd " + Ashes.Text.fromInt(position), at = position))

let recursive scan (limit: Int) (position: Int) (tokens: List(Tok)) (diagnostics: List(Diag)) =
    if position >= limit
    then (tokens, diagnostics)
    else
        match readNext(position) with
            | (token, diagnostic) ->
                scan(limit)(position + 1)(token :: tokens)(if position - position / 7 * 7 == 0
                then diagnostic :: diagnostics
                else diagnostics)

let recursive count (xs: List(Tok)) (acc: Int) =
    match xs with
        | [] -> acc
        | token :: rest -> count(rest)(acc + Ashes.Text.byteLength(token.text))

let recursive weigh (xs: List(Diag)) (acc: Int) =
    match xs with
        | [] -> acc
        | diagnostic :: rest -> weigh(rest)(acc + diagnostic.at + Ashes.Text.byteLength(diagnostic.message))

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        match scan(2000)(0)([])([]) with
            | (tokens, diagnostics) -> rounds(n - 1)(total + count(tokens)(0) + weigh(diagnostics)(0))

0
|> rounds(3)
|> Ashes.IO.print
