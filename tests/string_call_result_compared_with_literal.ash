// expect: 3000
type Token =
    | kind: Int
    | text: Str

let currentToken (tokens: List(Token)) =
    match tokens with
        | token :: _ -> token
        | [] -> Token(kind = 0, text = "")

let currentText (tokens: List(Token)) =
    (let current = currentToken(tokens)
    in current.text)

let classify (tokens: List(Token)) =
    if currentText(tokens) == "out"
    then 1
    else
        if currentText(tokens) == "FfiStr"
        then 2
        else 3

let recursive rounds (tokens: List(Token)) (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(tokens)(n - 1)(total + classify(tokens))

0
|> rounds([Token(kind = 1, text = "In" + "t")])(1000)
|> Ashes.IO.print
