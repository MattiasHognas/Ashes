// expect: 15
type TextSpan =
    | start: Int
    | end: Int

type Expr =
    | ExprAt(TextSpan, Expr)
    | ExprVar(Str)
    | ExprLet(Str, Expr, Expr)
    | ExprCall(Expr, Expr, Bool, Maybe(Int))

let recursive chainBodyFirst (body: Expr) (lastOrdinary: Bool) =
    match body with
        | ExprAt(_span, inner) -> chainBodyFirst(inner)(lastOrdinary)
        | ExprLet(_name, _value, nested) -> chainBodyFirst(nested)(true)
        | _ ->
            if lastOrdinary
            then Some(body)
            else None

let recursive chainBody (body: Expr) (lastOrdinary: Bool) =
    match body with
        | ExprAt(_span, inner) -> chainBody(inner)(lastOrdinary)
        | ExprLet(_name, _value, nested) -> chainBody(nested)(true)
        | _ ->
            if lastOrdinary
            then Some(body)
            else None

let recursive size (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> 1 + size(inner)
        | ExprLet(_name, value, body) -> size(value) + size(body)
        | ExprCall(callee, argument, _sugar, _layout) -> size(callee) + size(argument)
        | _ -> 1

let request (body: Expr) (n: Int) =
    match chainBody(body)(true) with
        | Some(found) -> size(found) + n
        | None -> n

let recursive build (k: Int) (n: Int) =
    if k == 0
    then
        ExprCall(ExprAt(TextSpan(start = 2, end = 3))(ExprVar("walk")))(ExprVar("rest"))(false)(Some(n))
    else
        n
        |> build(k - 1)
        |> ExprAt(TextSpan(start = k, end = n))

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else
        let tree =
            n
            |> build(2)
            |> ExprLet("next")(ExprVar("x"))
        in loop(n - 1)(acc + request(tree)(n))
in
    0
    |> loop(3)
    |> Ashes.Text.fromInt
    |> Ashes.IO.print
