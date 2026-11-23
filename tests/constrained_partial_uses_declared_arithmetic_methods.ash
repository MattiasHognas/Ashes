// expect: -1 17
type Box =
    | Box(Int)

let unpack boxed =
    match boxed with
        | Box(inner) -> inner

implement Add(Box) =
    | add =
        given (left) ->
            given (right) -> Box(unpack(left) + unpack(right))

implement Negate(Box) =
    | negate =
        given (value) -> Box(unpack(value) + 3)

implement BitwiseNot(Box) =
    | bitwiseNot =
        given (value) -> Box(unpack(value) + 4)

let combine : Unit -> a -> a requires {Add(a), BitwiseNot(a), Negate(a)} =
    given (_marker) ->
        given (value) -> -value + ~value

let run = combine(Unit)

Ashes.IO.print(Ashes.Text.fromInt(run(0)) + " " + Ashes.Text.fromInt(Box(5)
|> run
|> unpack))
