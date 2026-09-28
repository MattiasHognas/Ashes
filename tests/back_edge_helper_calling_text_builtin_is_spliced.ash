// A helper called in a loop's back edge calls a qualified built-in (`Ashes.Text.byteLength`) and
// returns a fresh list: it is spliced into the back edge, so the successor it builds is the loop's
// own and the earlier one is released.
// expect: 42
let targetsOf (width: Int) (name: Str) =
    if Ashes.Text.byteLength(name) < width
    then [name + "a", name + "b"]
    else []

let recursive walk (stack: List(List(Str))) (seen: List(Str)) (n: Int) =
    match stack with
        | [] -> n
        | [] :: rest -> walk(rest)(seen)(n)
        | (name :: more) :: rest -> walk(targetsOf(3)(name) :: more :: rest)(name :: seen)(n + 1)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + walk([["x"], ["y"]])([])(0))

0
|> rounds(3)
|> Ashes.IO.print
