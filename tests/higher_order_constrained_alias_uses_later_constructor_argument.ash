// expect: 0 11
type Box =
    | Box(Int)

implement BitXor(Box) =
    | bitXor =
        given (left) ->
            given (right) ->
                match (left, right) with
                    | (Box(x), Box(y)) -> Box((x ^ y) + 11)

let cancel : Unit -> a -> a requires {BitXor(a)} =
    given (_marker) ->
        given (value) -> value ^ value

let clear = cancel(Unit)

let alias = clear

let apply f x = f(x)

let boxed =
    match apply(alias)(Box(19)) with
        | Box(inner) -> inner

Ashes.IO.print(Ashes.Text.fromInt(apply(alias)(42)) + " " + Ashes.Text.fromInt(boxed))
