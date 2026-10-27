// expect: 84

let targetsOf (width: Int) (name: Str) =
    if Ashes.Text.byteLength(name) < width
    then [name + "a", name + "b"]
    else []

let recursive walk (stack: List(List(Str))) (seen: List(Str)) (n: Int) =
    match stack with
        | [] -> n
        | [] :: rest -> walk(rest)(seen)(n)
        | (name :: more) :: rest -> walk(targetsOf(3)(name) :: more :: rest)(name :: seen)(n + 1)

let recursive pairs (stack: List(List(Str))) (seen: List(Str)) (n: Int) =
    match (stack, seen) with
        | ([], _s) -> n
        | ([] :: rest, s) -> pairs(rest)(s)(n)
        | ((name :: more) :: rest, s) -> pairs(more :: targetsOf(3)(name) :: rest)(name :: s)(n + 1)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + walk([["x"], ["y"]])([])(0) + pairs([["x"], ["y"]])([])(0))

0
|> rounds(3)
|> Ashes.IO.print
