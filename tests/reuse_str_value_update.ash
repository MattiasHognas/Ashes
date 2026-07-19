// expect: 100
// Repeatedly updating the same key of a map with fresh Str values on the reuse path exercises the
// in-place blob reclaim (CopyStringIntoOrFresh): each update reuses the dead old value blob in place
// when the new value fits and the blob is provably in the persistent blob region, else materializes a
// fresh blob. This asserts CORRECTNESS across the fits/grow/region-check branches — a corrupt reclaim
// (e.g. overwriting reclaimable arena memory) would misread the final value. The bounded-memory win
// (RSS flat vs linear) is measured out-of-band.
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
    else loop(i + 1)(lim)(Ashes.Collection.Map.set(cmp)(0)(Ashes.Text.fromInt(i))(m))

let seeded = Ashes.Collection.Map.set(cmp)(0)("seed")(Ashes.Collection.Map.empty)

let final = loop(0)(100)(seeded)
in
    match Ashes.Collection.Map.get(cmp)(0)(final) with
        | None -> Ashes.IO.print("none")
        | Some(v) -> Ashes.IO.print(v)
