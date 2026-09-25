// A call result compared through a derived `Eq` or `Ord` is owned by no binding: the method borrows
// both operands and returns a Bool that keeps nothing of them, so the caller releases the operand
// after the call. A value read out of a binding is borrowed by the comparison and stays with its
// owner, which may compare it again.
// expect: 223000
type Kind =
    | Ident
    | If
    | Keyword
    deriving {Eq, Ord}

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

let flag (condition: Bool) (weight: Int) =
    if condition
    then weight
    else 0

let score (state: State) =
    (let kind = currentKind(state)
    in flag(kind != If)(1) + flag(kind == Keyword)(2) + flag(kind > If)(4))

let recursive rounds (state: State) (n: Int) (total: Int) =
    if n == 0
    then total
    else
        let fresh = flag(currentKind(state) != If)(8) + flag(Keyword == currentKind(state))(16)
        in
            let ordered = flag(currentKind(state) < If)(32) + flag(currentKind(state) >= currentKind(state))(64)
            in
                let record = flag(current(state) == Token(kind = Keyword, position = 1))(128)
                in rounds(state)(n - 1)(total + fresh + ordered + record + score(state))

let state = State(tokens = [Token(kind = Keyword, position = 1), Token(kind = If, position = 2)], index = 0)

0
|> rounds(state)(1000)
|> Ashes.IO.print
