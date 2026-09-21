type Tok =
    | kind: Int
    | text: Str
    | position: Int

type Diag =
    | message: Str
    | at: Int

let readNext (position: Int) =
    if position - position / 7 * 7 == 0
    then (Tok(kind = 1, text = Ashes.Text.fromInt(position), position = position), Some(Diag(message = "odd " + Ashes.Text.fromInt(position), at = position)))
    else (Tok(kind = 2, text = Ashes.Text.fromInt(position), position = position), None)

let recursive scan (limit: Int) (position: Int) (tokens: List(Tok)) (diagnostics: List(Diag)) =
    if position >= limit
    then (tokens, diagnostics)
    else
        match readNext(position) with
            | (token, nextDiagnostic) ->
                let nextDiagnostics =
                    match nextDiagnostic with
                        | None -> diagnostics
                        | Some(value) -> value :: diagnostics
                in scan(limit)(position + 1)(token :: tokens)(nextDiagnostics)

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
|> rounds(ROUNDS)
|> Ashes.IO.print
