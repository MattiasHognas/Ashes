// expect: x,Ashes 1,5 x,Ashes 1:t0:t0 1000:t999:t0 10000:t9999:t0
// A loop's record parameter takes a cell consed onto its own list field each round, and the exit
// returns that field: the back edge copies the successor record and must leave the cell's tail
// to the one owner that releases it, at 1, 1,000 and 10,000 rounds.
type State =
    | refs: List(Str)

type Counted =
    | count: Int
    | refs: List(Str)

type Measured =
    | path: Str
    | refs: List(Str)

let recursive scan (tokens: List(Str)) (state: State) =
    match tokens with
        | [] -> state.refs
        | text :: rest -> scan(rest)(State(refs = text :: state.refs))

let recursive scanCounted (tokens: List(Str)) (state: Counted) =
    match tokens with
        | [] -> state.refs
        | text :: rest -> scanCounted(rest)(Counted(count = state.count + 1, refs = text :: state.refs))

let recursive scanMeasured (tokens: List(Str)) (state: Measured) =
    match tokens with
        | [] -> state.refs
        | text :: rest ->
            scanMeasured(rest)(Measured(path = "", refs = Ashes.Text.fromInt(Ashes.Text.length(text)) :: state.refs))

let recursive tokens (n: Int) (i: Int) =
    if i >= n
    then []
    else "t" + Ashes.Text.fromInt(i) :: tokens(n)(i + 1)

let recursive lastOf (xs: List(Str)) =
    match xs with
        | [] -> "none"
        | x :: [] -> x
        | _ :: rest -> lastOf(rest)

let firstOf (xs: List(Str)) =
    match xs with
        | [] -> "none"
        | x :: _ -> x

let summary (n: Int) =
    (let refs =
        scanCounted(tokens(n)(0))(Counted(count = 0, refs = []))
    in
        Ashes.Text.fromInt(Ashes.Collection.List.length(refs)) + ":" + firstOf(refs) + ":" + lastOf(refs))

let words = ["Ashes", "x"]

[
    State(refs = [])
    |> scan(words)
    |> Ashes.Text.join(","),
    Measured(path = "", refs = [])
    |> scanMeasured(words)
    |> Ashes.Text.join(","),
    Counted(count = 0, refs = [])
    |> scanCounted(words)
    |> Ashes.Text.join(","),
    summary(1),
    summary(1000),
    summary(10000)
]
|> Ashes.Text.join(" ")
|> Ashes.IO.print
