// expect: 80
type Expr =
    | Var(Str)
    | Call(Expr, Expr)

let recursive spine (expression: Expr) (arguments: List(Expr)) =
    match expression with
        | Call(function, argument) -> spine(function)(argument :: arguments)
        | root -> (root, arguments)

let recursive append (xs: List(Expr)) (ys: List(Expr)) =
    match xs with
        | [] -> ys
        | x :: rest -> x :: append(rest)(ys)

let recursive nodes (pending: List(Expr)) (count: Int) =
    match pending with
        | [] -> count
        | (Call(_function, _argument) as call) :: rest ->
            match spine(call)([]) with
                | (root, arguments) -> nodes(root :: append(arguments)(rest))(count + 1)
        | _ :: rest -> nodes(rest)(count + 1)

let probe (n: Int) =
    nodes([Var("c")
    |> Call(Var("h"))
    |> Call(Var("a")
    |> Call(Var("g"))
    |> Call(Var("f" + Ashes.Text.fromInt(n))))])(0)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

0
|> loop(10)
|> Ashes.IO.print
