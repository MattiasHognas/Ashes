let withTokens (state: Int) (tokens: List(Int)) =
    match state with
        | flag -> (tokens, flag)

let recursive build (n: Int) (acc: List(Int)) =
    if n == 0
    then acc
    else build(n - 1)(n :: acc)

let parseOne (state: Int) =
    match []
    |> build(20)
    |> withTokens(state) with
        | (tokens, flag) -> Ashes.Collection.List.length(tokens) + flag

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + parseOne(1))

0
|> rounds(ROUNDS)
|> Ashes.IO.print
