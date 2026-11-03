// expect: 3
type TextSpan =
    | start: Int
    | end: Int

type Expr =
    | ExprAt(TextSpan, Expr)
    | ExprVar(Str)
    | ExprCall(Expr, Expr, Bool, Maybe(Int))
    | ExprTuple(List(Expr))

let recursive unspanFirst (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> unspanFirst(inner)
        | other -> other

let recursive unspan (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> unspan(inner)
        | other -> other

let elementSlot (element: Expr) (base: Int) =
    match unspan(element) with
        | ExprVar(_name) -> Some(base)
        | _ -> None

let recursive elementSlots (elements: List(Expr)) (base: Int) =
    match elements with
        | [] -> []
        | element :: rest -> elementSlot(element)(base) :: elementSlots(rest)(base)

let slotsOf (scrutinee: Expr) (base: Int) =
    match unspan(scrutinee) with
        | ExprTuple(elements) -> elementSlots(elements)(base)
        | _ -> []

let recursive countSome (slots: List(Maybe(Int))) =
    match slots with
        | [] -> 0
        | Some(_s) :: rest -> 1 + countSome(rest)
        | None :: rest -> countSome(rest)

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
        let tree = build(2)(n)
        in
            loop(n - 1)(acc + countSome(slotsOf(tree)(n)) + countSome(slotsOf(ExprAt(TextSpan(start = 3, end = 4))(ExprTuple([ExprVar("a"), tree])))(n)))
in
    0
    |> loop(3)
    |> Ashes.Text.fromInt
    |> Ashes.IO.print
