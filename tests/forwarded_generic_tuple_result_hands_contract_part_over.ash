// A generic function returns another generic function's tuple result as it is. Its caller's request
// for the tuple's contract list goes on to the function that built the tuple, which hands the list
// over and releases the owned record behind it.
// expect: 77
type Item =
    | name: Str
    | n: Int

type Box =
    | items: List(Item)
    | count: Int

let make (k: Int) = [Item(name = "a" + Ashes.Text.fromInt(k), n = k)]

let makeBox (k: Int) = Box(items = make(k + 1), count = k)

let pick value (k: Int) =
    (let built = makeBox(k)
    in
        if k > 2
        then (value, make(k))
        else (value, built.items))

let forward value (k: Int) = pick(value)(k + 1)

let recursive loop (remaining: Int) (total: Int) =
    if remaining == 0
    then total
    else
        match forward(remaining)(remaining - remaining / 5 * 5) with
            | (m, items) -> loop(remaining - 1)(total + m + Ashes.Collection.List.length(items))

0
|> loop(11)
|> Ashes.IO.print
