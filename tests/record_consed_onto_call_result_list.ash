// expect: 9
// A record consed onto a function's reference-counted list result outside any recursion:
// the cell is reference-counted too, so the list it holds is released with it.
type LeakLive =
    | block: Int
    | liveIn: Bool

let recursive build (n: Int) =
    if n == 0
    then []
    else LeakLive(block = n, liveIn = true) :: build(n - 1)

let wrap (n: Int) = LeakLive(block = n, liveIn = false) :: build(n)

let recursive countLive (entries: List(LeakLive)) (total: Int) =
    match entries with
        | [] -> total
        | LeakLive { liveIn = true } :: rest -> countLive(rest)(total + 1)
        | _ :: rest -> countLive(rest)(total)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        rounds(n - 1)(total + countLive(wrap(3))(0))

0
|> rounds(3)
|> Ashes.IO.print
