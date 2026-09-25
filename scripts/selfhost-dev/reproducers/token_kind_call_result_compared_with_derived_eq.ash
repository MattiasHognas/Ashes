type Kind =
    | Ident
    | If
    | Let
    deriving {Eq}

type Token =
    | kind: Kind
    | position: Int
    deriving {Eq}

type State =
    | tokens: List(Token)
    | index: Int

let current (state: State) =
    match state.tokens with
        | token :: _ -> token
        | [] -> Token(kind = Ident, position = 0)

let currentKind (state: State) =
    (let token = current(state)
    in token.kind)

let recursive rounds (state: State) (n: Int) (total: Int) =
    if n == 0
    then total
    else
        if currentKind(state) != If
        then rounds(state)(n - 1)(total + 1)
        else rounds(state)(n - 1)(total + 2)

let state = State(tokens = [Token(kind = Let, position = 1), Token(kind = If, position = 2)], index = 0)

0
|> rounds(state)(ROUNDS)
|> Ashes.IO.print
