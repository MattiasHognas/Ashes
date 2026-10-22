// expect: 500

type Liveness =
    | block: Int
    | liveIn: Bool
    | liveOut: Bool

let make (b: Int) = Liveness(block = b, liveIn = true, liveOut = false)

let recursive step (remaining: List(Int)) =
    match remaining with
        | [] -> []
        | block :: rest ->
            let entry = make(block)
            in entry :: step(rest)

let recursive countLive (items: List(Liveness)) (total: Int) =
    match items with
        | [] -> total
        | Liveness { liveIn = true } :: rest -> countLive(rest)(total + 1)
        | _ :: rest -> countLive(rest)(total)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        rounds(n - 1)(total + countLive(step([0, 1, 2, 3, 4]))(0))

0
|> rounds(100)
|> Ashes.IO.print
