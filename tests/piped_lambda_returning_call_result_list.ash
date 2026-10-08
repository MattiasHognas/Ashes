// expect: 9
// A lambda applied in a pipe returns a recursive producer's reference-counted list; the
// function piping into it hands that list to its caller, which releases it.
type LeakLive =
    | block: Int
    | liveIn: Bool

let recursive build (n: Int) =
    if n == 0
    then []
    else LeakLive(block = n, liveIn = true) :: build(n - 1)

let recursive stepLive (remaining: List(Int)) =
    match remaining with
        | [] -> []
        | block :: rest ->
            block |> (given (m) -> build(m))

let recursive countLive (entries: List(LeakLive)) (total: Int) =
    match entries with
        | [] -> total
        | LeakLive { liveIn = true } :: rest -> countLive(rest)(total + 1)
        | _ :: rest -> countLive(rest)(total)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        rounds(n - 1)(total + countLive(stepLive([3, 2]))(0))

0
|> rounds(3)
|> Ashes.IO.print
