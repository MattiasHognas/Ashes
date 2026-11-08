// expect: 80
import Ashes.Collection.List.append
type Expr =
    | Var(Str)
    | Call(Expr, Expr)
    | At(Int, Expr)
    | Num(Int)

let recursive spine (expression: Expr) (arguments: List(Expr)) =
    match expression with
        | At(_span, inner) -> spine(inner)(arguments)
        | Call(function, argument) -> spine(function)(argument :: arguments)
        | root -> (root, arguments)

let recursive nodes (pending: List(Expr)) (found: List(Expr)) =
    match pending with
        | [] -> Some(found)
        | At(_span, inner) :: rest -> nodes(inner :: rest)(found)
        | (Call(_function, _argument) as call) :: rest ->
            match spine(call)([]) with
                | (root, arguments) -> nodes(root :: append(arguments)(rest))(call :: found)
        | Num(0) :: _ -> None
        | expression :: rest -> nodes(rest)(expression :: found)

let probe (n: Int) =
    match nodes([Num(n)
    |> Call(Var("h"))
    |> Call(Var("a")
    |> Call(Var("g"))
    |> Call(Var("f" + Ashes.Text.fromInt(n))))])([]) with
        | Some(found) -> Ashes.Collection.List.length(found)
        | None -> 0

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

0
|> loop(10)
|> Ashes.IO.print
