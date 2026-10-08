// expect: 3,2,1|1,2,3|3
let recursive collect (n: Int) (acc: List(Str)) =
    if n == 0
    then acc
    else collect(n - 1)(Ashes.Text.fromInt(n) :: acc)

let recursive pairs (previous: Str) (acc: List(Str)) (ps: List(Str)) =
    match ps with
        | [] -> acc
        | first :: rest -> pairs(first)(first :: acc)(rest)

let recursive skipLast (seen: List(Str)) (acc: List(Str)) (ps: List(Str)) =
    match ps with
        | [] -> Ashes.Collection.List.reverse(acc)
        | first :: rest -> skipLast(rest)(first :: acc)(rest)

let recursive mark (label: Str) (count: Int) (ps: List(Str)) =
    match ps with
        | [] -> count
        | first :: rest -> mark(first + "!")(count + 1)(rest)

let parts = collect(3)([])

Ashes.IO.print(Ashes.Text.join(",")(pairs("x")([])(parts)) + "|" + Ashes.Text.join(",")(skipLast([])([])(parts)) + "|" + Ashes.Text.fromInt(mark("")(0)(parts)))
