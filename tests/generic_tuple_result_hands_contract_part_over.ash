// A generic function returns a tuple pairing its type parameter's value with a contract list: a
// call's result, a borrowed parameter, a fresh list or a field of an owned record. Its caller asks
// for the list and takes it over, so the function releases the owned values behind it.
// expect: 33
type Item =
    | name: Str
    | n: Int

type Box =
    | items: List(Item)
    | count: Int

let make (k: Int) = [Item(name = "a" + Ashes.Text.fromInt(k), n = k)]

let makeBox (k: Int) = Box(items = make(k + 1), count = k)

let pick value (k: Int) (supplied: List(Item)) =
    (let built = makeBox(k)
    in
        if k > 3
        then (value, make(k))
        else
            if k > 2
            then (value, supplied)
            else
                if k > 1
                then (value, [Item(name = "f" + Ashes.Text.fromInt(k), n = k)])
                else (value, built.items))

let recursive loop (remaining: Int) (total: Int) =
    if remaining == 0
    then total
    else
        let supplied = make(remaining + 7)
        in
            match pick(remaining)(remaining - remaining / 5 * 5)(supplied) with
                | (m, items) -> loop(remaining - 1)(total + m + Ashes.Collection.List.length(items) + Ashes.Collection.List.length(supplied))

0
|> loop(6)
|> Ashes.IO.print
