let make (n: Int) (xs: List((Int, Int))) = ((n, 1) :: xs, n)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        match make(n)([]) with
            | (s, r) -> rounds(n - 1)(total + r)

0
|> rounds(ROUNDS)
|> Ashes.IO.print
