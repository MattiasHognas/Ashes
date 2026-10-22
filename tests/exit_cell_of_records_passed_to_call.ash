// expect: 9000
type Token =
    | text: Str
    | position: Int

let recursive reverseValues remaining result =
    match remaining with
        | [] -> result
        | head :: tail -> reverseValues(tail)(head :: result)

let readToken (position: Int) = Token(text = Ashes.Text.fromInt(position), position = position)

let recursive scan (n: Int) (position: Int) (tokens: List(Token)) =
    if position >= n
    then reverseValues(Token(text = "", position = position) :: tokens)([])
    else
        let token = readToken(position)
        in scan(n)(token.position + 1)(token :: tokens)

let recursive measure (tokens: List(Token)) (total: Int) =
    match tokens with
        | [] -> total
        | token :: rest -> measure(rest)(total + Ashes.Text.byteLength(token.text) + token.position)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        rounds(n - 1)(total + measure(scan(3)(0)([]))(0))

0
|> rounds(1000)
|> Ashes.IO.print
