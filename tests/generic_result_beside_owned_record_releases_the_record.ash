// A function returning a generic list builds an owned record it only tests; the record is out of
// the result's reach and is released at the return.
// expect: 6
type Box =
    | items: List(Str)
    | count: Int

let make (n: Int) = Box(items = ["a" + Ashes.Text.fromInt(n), "b"], count = n)

let keepIfLarge (value: a) (n: Int) =
    (let box = make(n)
    in
        if box.count > 2
        then [value, value]
        else [])

let recursive loop (remaining: Int) (total: Int) =
    if remaining == 0
    then total
    else
        loop(remaining - 1)(total + Ashes.Collection.List.length(keepIfLarge(remaining)(remaining)))

0
|> loop(5)
|> Ashes.IO.print
