// expect: 100,101
// Repeatedly updating the same key of a map with fresh tuple (Int, Int) values on the reuse path
// exercises the region-guarded fixed-size blob reclaim (CopyFixedIntoOrFresh): each update overwrites
// the dead old value cell in place when it is provably in the persistent blob region, else fresh.
// Asserts correctness across the region-check branches; the bounded-memory win is measured out-of-band.
import Ashes.Collection.Map
import Ashes.IO
import Ashes.Text
let cmp a b =
    if a == b
    then 0
    else
        if a <= b
        then -1
        else 1

let recursive loop i lim m =
    if i > lim
    then m
    else loop(i + 1)(lim)(Ashes.Collection.Map.set(cmp)(0)((i, i + 1))(m))

let seeded = Ashes.Collection.Map.set(cmp)(0)((0, 0))(Ashes.Collection.Map.empty)

let final = loop(0)(100)(seeded)
in
    match Ashes.Collection.Map.get(cmp)(0)(final) with
        | None -> Ashes.IO.print("none")
        | Some(pair) ->
            match pair with
                | (a, b) -> Ashes.IO.print(Ashes.Text.fromInt(a) + "," + Ashes.Text.fromInt(b))
