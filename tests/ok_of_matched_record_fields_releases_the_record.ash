// expect: 600
type Fields =
    | xs: List(Int)
    | ps: List((Int, Bool))
    | js: List(Str)

let make (k: Int) = Fields(xs = [k, k + 1], ps = [(k, true)], js = ["j"])

let outer (k: Int) =
    match make(k) with
        | Fields { xs = xs, ps = ps, js = js } -> Ok((xs, (ps, js)))

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        match outer(n) with
            | Ok((a, (b, c))) -> rounds(n - 1)(total + Ashes.Collection.List.length(a) + Ashes.Collection.List.length(b) + Ashes.Collection.List.length(c) - 2)
            | Error(_) -> rounds(n - 1)(total)

0
|> rounds(300)
|> Ashes.IO.print
