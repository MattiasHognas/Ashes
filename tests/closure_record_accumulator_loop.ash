// expect: 1-abcdefghijklmnop
// A loop carrying a record whose closure captures the iteration's string rebuilds the record
// every iteration: each previous accumulator is released with its closure and captured string,
// and the final record still reads its own capture after the loop.

type Box =
    | reader: Int -> Str

let recursive loop (n: Int) (acc: Box) =
    if n == 0
    then acc
    else
        let text = Ashes.Text.fromInt(n) + "-abcdefghijklmnop"
        in
            loop(n - 1)(Box(reader = given (u: Int) -> text))

match loop(50000)(Box(reader = given (u: Int) -> "seed")) with
    | Box { reader = reader } ->
        0
        |> reader
        |> Ashes.IO.print
