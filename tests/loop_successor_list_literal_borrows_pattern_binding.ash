// expect: Ashes x 1:t0 1000:t999 10000:t9999
// A loop's successor record holds a list literal of a name matched out of the list the loop
// walks (`State(refs = [text])`). The literal only borrows the name, whose own owner releases it,
// so the back edge's copy of the successor releases no reference for it, at 1, 1,000 and 10,000
// rounds.
type State =
    | refs: List(Str)

let recursive scan (tokens: List(Str)) (state: State) =
    match tokens with
        | [] -> state.refs
        | text :: rest -> scan(rest)(State(refs = [text]))

let recursive tokens (n: Int) (i: Int) =
    if i >= n
    then []
    else "t" + Ashes.Text.fromInt(i) :: tokens(n)(i + 1)

let lastToken (n: Int) =
    match scan(tokens(n)(0))(State(refs = [])) with
        | [] -> "none"
        | x :: _ -> x

let single =
    State(refs = [])
    |> scan(["Ashes"])
    |> Ashes.Text.join(",")

let pair =
    State(refs = [])
    |> scan(["Ashes", "x"])
    |> Ashes.Text.join(",")

[single, pair, "1:" + lastToken(1), "1000:" + lastToken(1000), "10000:" + lastToken(10000)]
|> Ashes.Text.join(" ")
|> Ashes.IO.print
