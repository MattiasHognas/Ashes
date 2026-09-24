// A recursive group whose siblings return a pair of a record and a state holding a token list: a
// sibling's result type is unresolved where the call is lowered, and once it turns out to be one
// the caller owns, the callee hands it over instead of copying it, list and all, into the arena.
// expect: 39980000
type Kind =
    | EOF
    | Word
    | Num

type Tok =
    | kind: Kind
    | text: Str
    | position: Int

type alias State = (List(Tok), List(Str), Bytes, Bool)

let makeTok (kind: Kind) (text: Str) (position: Int) = Tok(kind = kind, text = text, position = position)

let recursive lexAll (position: Int) (count: Int) (acc: List(Tok)) =
    if position >= count
    then acc
    else
        lexAll(position + 1)(count)(makeTok(if position % 2 == 0
        then Word
        else Num)(Ashes.Text.fromInt(position))(position) :: acc)

let current (state: State) =
    match state with
        | (token :: _, _notes, _source, _flag) -> token
        | ([], _notes, _source, _flag) -> makeTok(EOF)("")(0)

let advance (state: State) =
    match state with
        | (token :: tail, notes, source, flag) -> (token, (tail, notes, source, flag))
        | ([], _notes, _source, _flag) -> (makeTok(EOF)("")(0), state)

let isEof (token: Tok) =
    match token.kind with
        | EOF -> true
        | _ -> false

let consume (state: State) =
    (let token = current(state)
    in
        if isEof(token)
        then (makeTok(Word)("")(token.position), state)
        else advance(state))

let recursive parseGroup (state: State) =
    match consume(state) with
        | (opening, afterOpen) ->
            match parseItem(afterOpen) with
                | (value, afterValue) ->
                    match consume(afterValue) with
                        | (closing, afterClose) -> (value + opening.position + closing.position, afterClose)
and parseItem state =
    (let token = current(state)
    in
        match token.kind with
            | Num ->
                match advance(state) with
                    | (token, next) -> (token.position, next)
            | _ -> parseGroup(state))

let recursive parseAll (state: State) (total: Int) =
    if state
    |> current
    |> isEof
    then total
    else
        match parseGroup(state) with
            | (value, next) -> parseAll(next)(total + value)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + parseAll((lexAll(0)(2000)([]), [], Ashes.Byte.fromText("abc"), false))(0))

0
|> rounds(20)
|> Ashes.IO.print
