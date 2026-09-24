type alias State = (List(Int), List(Str), Bytes, Bool)

let withTokens (state: State) (tokens: List(Int)) =
    match state with
        | (_old, notes, source, flag) -> (tokens, notes, source, flag)

let tokensOf (state: State) =
    match state with
        | (tokens, _notes, _source, _flag) -> tokens

let recursive build (n: Int) (acc: List(Int)) =
    if n == 0
    then acc
    else build(n - 1)(n :: acc)

let parseOne (state: State) =
    (let temporary =
        Ashes.Collection.List.append(build(20)([]))([0])
    in
        let temporaryState = withTokens(state)(temporary)
        in
            temporaryState
            |> tokensOf
            |> Ashes.Collection.List.length)

let recursive rounds (n: Int) (state: State) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(state)(total + parseOne(state))

0
|> rounds(ROUNDS)(([], [], Ashes.Byte.fromText("abc"), false))
|> Ashes.IO.print
