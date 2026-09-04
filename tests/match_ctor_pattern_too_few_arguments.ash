// expect-compile-error: Constructor 'Leaf' expects 2 argument(s) but pattern has 1.
type Plan =
    | Leaf(Int, Int)
    | Empty

let describe (plan: Plan) =
    match plan with
        | Leaf(x) -> x
        | Empty -> 0

Ashes.IO.print(Ashes.Text.fromInt(describe(Leaf(1)(2))))
