// The `Ord` form of `let_in_self_call_operand_released_once.ash`: the `let` inlined into an operand
// of the loop's self call compares its `kind` with a derived `>`, and is released at its scope exit
// only. A second release at the back edge frees the token's kind, and the program faults or hangs
// on the corrupted free list.
// expect: 1000
type Kind =
    | Plain
    | Keyword
    deriving {Eq, Ord}

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
        if kind > Plain
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
