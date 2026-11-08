// expect: 24
type Pat =
    | PVar(Str)
    | PTuple(List(Pat))

type Acc =
    | count: Int
    | labels: List(Str)
    | seen: List(Str)

let recursive step (pats: List(Pat)) (acc: Acc) =
    match (pats, acc) with
        | ([], _) -> acc
        | (pat :: rest, Acc { count = count, labels = labels, seen = seen }) -> step(rest)(Acc(count = count + 2, labels = labels, seen = seen))

let run (seed: Int) =
    match step([PVar("a"), PTuple([PVar("b")]), PVar("c")])(Acc(count = seed, labels = ["x", "y"], seen = ["z"])) with
        | Acc { count = c } -> c

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + run(n))

0
|> rounds(3)
|> Ashes.IO.print
