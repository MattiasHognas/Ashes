// A `let` whose scope closes inside an operand of a loop's self call releases its owned value at
// its scope exit; the back edge that follows releases only the owners still in scope. Here `score`
// is inlined into the loop, and its `kind` is the token's own field: a second release at the back
// edge would free the token's kind, and every later comparison would read a freed cell.
// expect: 1000
type Kind =
    | Plain
    | Keyword
    deriving {Eq}

type Token =
    | kind: Kind
    | position: Int

type State =
    | tokens: List(Token)
    | index: Int

let current (state: State) =
    match state.tokens with
        | token :: _ -> token
        | [] -> Token(kind = Plain, position = 0)

let currentKind (state: State) =
    (let token = current(state)
    in token.kind)

let score (state: State) =
    (let kind = currentKind(state)
    in
        if kind == Keyword
        then 1
        else 0)

let recursive rounds (state: State) (n: Int) (total: Int) =
    if n == 0
    then total
    else
        let extra =
            if currentKind(state) == Plain
            then 5
            else 0
        in rounds(state)(n - 1)(total + extra + score(state))

let state = State(tokens = [Token(kind = Keyword, position = 1)], index = 0)

0
|> rounds(state)(1000)
|> Ashes.IO.print
