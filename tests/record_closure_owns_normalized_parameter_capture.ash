// expect: 2
// A record returned with a closure over the entry-normalized string parameter owns the closure, and
// the closure owns the parameter: releasing the record releases both.
type Box =
    | reader: Int -> Str

let box (s: Str) =
    Box(reader = given (u: Int) -> s)

let k =
    match box(Ashes.Text.fromInt(5) + "x") with
        | Box { reader = reader } ->
            0
            |> reader
            |> Ashes.Text.byteLength

k
|> Ashes.Text.fromInt
|> Ashes.IO.print
