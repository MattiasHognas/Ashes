// A function whose body is a let around an if joining a call's owned pair with a freshly built one:
// the join holds a reference of its own, and the let passing it on keeps that, so the function's
// return takes the pair over instead of retaining it a second time.
// expect: 53266700
type Kind =
    | EOF
    | Word
    | Num

type Tok =
    | kind: Kind
    | text: Str
    | position: Int

type Note =
    | position: Int
    | message: Str
    | code: Maybe(Str)

type alias State = (List(Tok), List(Note), Bytes, Bool)

let makeTok (kind: Kind) (text: Str) (position: Int) = Tok(kind = kind, text = text, position = position)

let recursive lexAll (position: Int) (count: Int) (acc: List(Tok)) =
    if position >= count
    then acc
    else
        lexAll(position + 1)(count)(makeTok(if position % 3 == 0
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

let noted (state: State) (token: Tok) (note: Str) =
    (let entry = Note(position = token.position, message = note, code = Some("E1"))
    in
        match state with
            | (tokens, notes, source, flag) -> (tokens, entry :: notes, source, flag))

let isKind (expected: Kind) (token: Tok) =
    match (expected, token.kind) with
        | (EOF, EOF) -> true
        | (Word, Word) -> true
        | (Num, Num) -> true
        | _ -> false

let consume (expected: Kind) (state: State) =
    (let token = current(state)
    in
        if isKind(expected)(token)
        then advance(state)
        else
            let message = "Expected " + token.text + " here."
            in (makeTok(expected)("")(token.position), noted(state)(token)(message)))

let recursive parseAll (state: State) (total: Int) =
    if state
    |> current
    |> isKind(EOF)
    then total
    else
        match consume(Num)(state) with
            | (token, next) ->
                match advance(next) with
                    | (other, afterOther) -> parseAll(afterOther)(total + token.position + other.position)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + parseAll((lexAll(0)(2000)([]), [], Ashes.Byte.fromText("abc"), false))(0))

0
|> rounds(20)
|> Ashes.IO.print
