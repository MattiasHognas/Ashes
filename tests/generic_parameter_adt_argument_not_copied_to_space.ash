// expect: 18
type Expr =
    | ExprAt(Int, Expr)
    | ExprVar(Str)

let recursive walk (items: List(Int)) trailing (total: Int) =
    match items with
        | [] -> total
        | item :: rest -> walk(rest)(trailing)(total + item)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else
        let trailing = ExprAt(n)(ExprVar("z"))
        in loop(n - 1)(acc + walk([1, 2, 3])(trailing)(0))
in
    0
    |> loop(3)
    |> Ashes.Text.fromInt
    |> Ashes.IO.print
