// expect-compile-error: Constructor 'Leaf' expects 2 argument(s) but pattern has 1.
type Plan =
    | Leaf(Int, Int)
    | Empty

let describe (plan: Plan) =
    match plan with
        | Leaf(x) -> x
        | Empty -> 0

2
|> Leaf(1)
|> describe
|> Ashes.Text.fromInt
|> Ashes.IO.print
