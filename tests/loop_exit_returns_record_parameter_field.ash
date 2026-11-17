// expect: x,Ashes x,Ashes 1:t0 1000:t999 10000:t9999
// A loop's exit returns a list field of its record parameter (`state.refs`), from a match arm and
// from an `if` branch, while the exit releases that parameter: the returned field keeps a
// reference of its own, at 1, 1,000 and 10,000 rounds.
type State =
    | refs: List(Str)

let recursive scanMatch (tokens: List(Str)) (state: State) =
    match tokens with
        | [] -> state.refs
        | text :: rest -> scanMatch(rest)(State(refs = text :: state.refs))

let recursive scanIf (tokens: List(Str)) (state: State) =
    if Ashes.Collection.List.length(tokens) == 0
    then state.refs
    else
        match tokens with
            | [] -> []
            | text :: rest -> scanIf(rest)(State(refs = text :: state.refs))

let recursive tokens (n: Int) (i: Int) =
    if i >= n
    then []
    else "t" + Ashes.Text.fromInt(i) :: tokens(n)(i + 1)

let firstOf (n: Int) =
    match scanIf(tokens(n)(0))(State(refs = [])) with
        | [] -> "none"
        | x :: _ -> x

let words = ["Ashes", "x"]

let matched =
    State(refs = [])
    |> scanMatch(words)
    |> Ashes.Text.join(",")

let branched =
    State(refs = [])
    |> scanIf(words)
    |> Ashes.Text.join(",")

[matched, branched, "1:" + firstOf(1), "1000:" + firstOf(1000), "10000:" + firstOf(10000)]
|> Ashes.Text.join(" ")
|> Ashes.IO.print
