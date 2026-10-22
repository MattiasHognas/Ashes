// A function hands an owned record a call built to a function returning an opaque foreign handle;
// the handle cannot hold the record, which is released at the return.
// expect: 5
external type Handle
external handleOf(Str) -> Handle = "strlen"

type Box =
    | name: Str
    | items: List(Str)

let make (n: Int) = Box(name = "box" + Ashes.Text.fromInt(n), items = ["a" + Ashes.Text.fromInt(n), "b"])

let register (box: Box) = handleOf(box.name)

let registerNew (n: Int) =
    n
    |> make
    |> register

let recursive loop (remaining: Int) (total: Int) =
    if remaining == 0
    then total
    else
        match registerNew(remaining) with
            | _handle -> loop(remaining - 1)(total + 1)

0
|> loop(5)
|> Ashes.IO.print
