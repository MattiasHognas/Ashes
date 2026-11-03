// expect: 18
type Pattern =
    | PatternVar(Str)
    | PatternWildcard
    | PatternAt(Int, Pattern)

type Expr =
    | ExprAt(Int, Expr)
    | ExprInt(Int)
    | ExprVar(Str)
    | ExprMatch(Expr, List((Pattern, Expr, Maybe(Expr))), Maybe(Int))

let recursive inlinePlainVarAliases (expression: Expr) =
    match expression with
        | ExprAt(span, inner) ->
            inner
            |> inlinePlainVarAliases
            |> ExprAt(span)
        | ExprMatch(scrutinee, cases, position) ->
            ExprMatch(scrutinee)(inlinePlainVarAliasesCases(cases))(position)
        | other -> other
and inlinePlainVarAliasesCases (cases: List((Pattern, Expr, Maybe(Expr)))) =
    match cases with
        | [] -> []
        | (pattern, body, guard) :: rest -> (pattern, body, guard) :: inlinePlainVarAliasesCases(rest)

let recursive exprSize (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> 1 + exprSize(inner)
        | ExprMatch(scrutinee, cases, _position) -> exprSize(scrutinee) + casesSize(cases)
        | _ -> 1
and casesSize (cases: List((Pattern, Expr, Maybe(Expr)))) =
    match cases with
        | [] -> 0
        | (_pattern, body, _guard) :: rest -> exprSize(body) + casesSize(rest)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else
        let body =
            None
            |> ExprMatch(ExprAt(1)(ExprInt(n)))([(PatternVar("a"), ExprAt(2)(ExprInt(n)), None), (PatternWildcard, ExprInt(4), Some(ExprInt(3)))])
            |> ExprAt(n)
        in
            loop(n - 1)(acc + exprSize(inlinePlainVarAliases(body)))
in
    0
    |> loop(3)
    |> Ashes.Text.fromInt
    |> Ashes.IO.print
