// expect: 5
// Regression: a nested `let recursive` that conses a call result built from each matched head onto
// its own call on the matched tail. The self call reads no ownership summary, so the tail is
// handed to it under the callee's adoption bit rather than retained outright; a forced retain was
// adopted by the callee and released again by the caller, freeing the tail twice. A list of two
// names is the shape that freed a live cell.
type Pattern =
    | PatternWildcard
    | PatternConstructor(Str, List(Pattern))

type Expr =
    | ExprBool(Bool)
    | ExprMatch(Expr, List((Pattern, Expr, Maybe(Expr))), Maybe(Int))

let orderingCase name = (PatternConstructor(name)([]), ExprBool(true), None)

let ordPredicateDefault accepted =
    (let recursive acceptedCases names =
        match names with
            | [] -> [(PatternWildcard, ExprBool(false), None)]
            | name :: tail -> orderingCase(name) :: acceptedCases(tail)
    in
        ExprMatch(ExprBool(true))(acceptedCases(accepted))(None))

let count e =
    match e with
        | ExprMatch(_, cases, _) -> Ashes.Collection.List.length(cases)
        | _ -> 0

let a = ["Less"] |> ordPredicateDefault

let b = ["Less", "Equal"] |> ordPredicateDefault

count(a) + count(b) |> Ashes.IO.print
