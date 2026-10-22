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

let apply f (k: Int) = f(k)(k)

let recursive loop (remaining: Int) (total: Int) =
    if remaining == 0
    then total
    else
        match apply(pick)(remaining - remaining / 5 * 5) with
            | (m, items) -> loop(remaining - 1)(total + m + Ashes.Collection.List.length(items))

0
|> loop(ROUNDS * 5)
|> Ashes.IO.print
