// expect: 500500
// A tuple consed onto a parameter list inside a tuple result stays in the arena with the list cell
// holding it. It used to inherit the enclosing tuple's reference-counted representation, and the
// arena cell was reclaimed without releasing it.
let make (n: Int) (xs: List((Int, Int))) = ((n, 1) :: xs, n)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        match make(n)([]) with
            | (_s, r) -> rounds(n - 1)(total + r)

0
|> rounds(1000)
|> Ashes.IO.print
