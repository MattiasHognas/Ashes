// expect: 6
// A loop splits a call into its root and argument list, handing the list on in one self-call and
// consing onto it in the other, and returns both in a tuple; the caller pushes the root and the
// arguments back onto its worklist.
type Expr =
    | ExprVar(Str)
    | ExprInt(Int)
    | ExprAt(Int, Expr)
    | ExprCall(Expr, Expr, Bool, Int)

let recursive shapeCallSpine (expression: Expr) (arguments: List(Expr)) =
    match expression with
        | ExprAt(_span, inner) -> shapeCallSpine(inner)(arguments)
        | ExprCall(function, argument, _sugar, _layout) -> shapeCallSpine(function)(argument :: arguments)
        | root -> (root, arguments)

let recursive append (xs: List(Expr)) (ys: List(Expr)) =
    match xs with
        | [] -> ys
        | x :: rest -> x :: append(rest)(ys)

let recursive countNodes (pending: List(Expr)) (n: Int) =
    match pending with
        | [] -> n
        | (ExprCall(_function, _argument, _sugar, _layout) as call) :: rest ->
            match shapeCallSpine(call)([]) with
                | (root, arguments) -> countNodes(root :: append(arguments)(rest))(n + 1)
        | _expression :: rest -> countNodes(rest)(n + 1)

let probe (k: Int) =
    countNodes([ExprCall(ExprCall(ExprVar("walk" + Ashes.Text.fromInt(k)))(ExprVar("rest"))(false)(0))(ExprInt(k))(false)(0)])(0)

Ashes.IO.print(probe(1) + probe(2) - 2)
