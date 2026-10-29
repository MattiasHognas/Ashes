// expect: 114
type Expr =
    | Num(Int)
    | Bound(Str, Expr)

let recursive items (acc: List(Int)) (n: Int) =
    if n > 0
    then top(acc)(n)
    else (acc, None, "done")
and top acc n =
    if n == 3
    then
        (acc, Num(n)
        |> Bound("x")
        |> Some, "found")
    else items(n :: acc)(n - 1)

let recursive size (xs: List(Int)) (total: Int) =
    match xs with
        | [] -> total
        | _ :: rest -> size(rest)(total + 1)

let run (seed: Int) =
    match items([])(seed) with
        | (found, body, _) ->
            size(found)(0) + (match body with
                | Some(Bound(_name, Num(value))) -> value
                | Some(_) -> 0
                | None -> 100)

let recursive rounds (n: Int) (total: Int) =
    match n with
        | 0 -> total
        | _ -> rounds(n - 1)(total + run(n + 1))

0
|> rounds(4)
|> Ashes.IO.print
