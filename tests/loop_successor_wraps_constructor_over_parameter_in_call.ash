// expect: 1000
type Span =
    | start: Int
    | finish: Int

type Expr =
    | ExprVar(Str)
    | ExprQualified(Str, Str)
    | ExprCall(Expr, Expr, Bool, Int)
    | ExprAt(Span, Expr)

let layoutOf (isMultiline: Bool) (isFirst: Bool) =
    match (isMultiline, isFirst) with
        | (false, _) -> 0
        | (true, true) -> 1
        | (true, false) -> 2

let parserAt start end expression = ExprAt(Span(start = start, finish = end))(expression)

let buildCall function arguments start end isWhitespace isMultiline =
    (let recursive build current remaining isFirst =
        match remaining with
            | [] -> current
            | argument :: tail ->
                let layout = layoutOf(isMultiline)(isFirst)
                in
                    build(layout
                    |> ExprCall(current)(argument)(isWhitespace)
                    |> parserAt(start)(end))(tail)(false)
    in build(function)(arguments)(true))

let recursive depth (expression: Expr) =
    match expression with
        | ExprAt(_, inner) -> depth(inner)
        | ExprCall(f, _, _, _) -> 1 + depth(f)
        | _ -> 0

let parse (n: Int) =
    (let function =
        "print"
        |> ExprQualified("Ashes." + Ashes.Text.fromInt(n))
        |> parserAt(0)(14)
    in
        false
        |> buildCall(function)([ExprVar("x" + Ashes.Text.fromInt(n))])(0)(17)(false)
        |> depth)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + parse(n))

0
|> rounds(1000)
|> Ashes.IO.print
