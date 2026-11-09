// expect: 6
type Expr =
    | ExprAt(Int, Expr)
    | ExprVar(Str)

let recursive walk (items: List(Int)) trailing (total: Int) =
    match items with
        | [] -> total
        | item :: rest -> walk(rest)(trailing)(total + item)

0
|> walk([1, 2, 3])(ExprAt(1)(ExprVar("z")))
|> Ashes.IO.print
