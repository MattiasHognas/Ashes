type Tok =
    | kind: Int
    | text: Str
    | position: Int

let readNext (position: Int) = Tok(kind = 2, text = Ashes.Text.fromInt(position), position = position)

let recursive scan (limit: Int) (position: Int) (tokens: List(Tok)) =
    if position >= limit
    then (tokens, position)
    else scan(limit)(position + 1)(readNext(position) :: tokens)

let recursive count (xs: List(Tok)) (acc: Int) =
    match xs with
        | [] -> acc
        | _ :: rest -> count(rest)(acc + 1)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        match scan(2000)(0)([]) with
            | (tokens, _position) -> rounds(n - 1)(total + count(tokens)(0))

0
|> rounds(ROUNDS)
|> Ashes.IO.print
