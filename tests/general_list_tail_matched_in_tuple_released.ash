// expect: 12
type Pat =
    | PVar(Str)
    | PTuple(List(Pat))

type Res =
    | total: Int
    | names: List(Str)

let recursive walk (pats: List(Pat)) (result: Res) =
    match (pats, result) with
        | ([], _) -> result
        | (pat :: rest, Res { total = total, names = names }) -> walk(rest)(Res(total = total + 1, names = names))

let run (seed: Int) =
    match walk([PVar("a"), PTuple([PVar("b")])])(Res(total = seed, names = ["x"])) with
        | Res { total = t } -> t

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + run(n))

0
|> rounds(3)
|> Ashes.IO.print
