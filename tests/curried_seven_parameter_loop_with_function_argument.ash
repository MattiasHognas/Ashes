// expect: 5000
// A curried seven-parameter loop that threads a function argument and a record state. On a host
// with AVX-512, LLVM builds the loop's closure environment from a 64-byte vector constant loaded
// with an aligned instruction, so the linker must place that constant at its declared alignment.
type Expr =
    | ExprVar(Str)
    | ExprAdd(Expr, Expr)
    | ExprInt(Int)

type State =
    | slot: Int
    | count: Int

let withSlot value (state: State) = state with slot = value

let ensure (t: Int) (state: State) = state

let lowerOne (argument: Expr) (state: State) =
    match argument with
        | ExprInt(v) -> state with count = state.count + v
        | _ -> state

let recursive len (xs: List(Int)) =
    match xs with
        | [] -> 0
        | _x :: rest -> 1 + len(rest)

let restSlotsOf (slots: List(Int)) =
    match slots with
        | _slot :: rest -> rest
        | [] -> []

let recursive loop (arguments: List(Expr)) (slots: List(Int)) (ordinal: Int) functionType lower (state: State) (temps: List(Int)) =
    match arguments with
        | [] -> state.count + len(temps)
        | argument :: rest ->
            loop(rest)(restSlotsOf(slots))(ordinal)(functionType)(lower)(state
            |> ensure(0)
            |> lower(argument)
            |> withSlot(0))(temps)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + loop([ExprInt(1), ExprVar("a"), ExprInt(4)])([1, 2, 3])(0)(1)(lowerOne)(State(slot = 0, count = 0))([]))

0
|> rounds(1000)
|> Ashes.IO.print
