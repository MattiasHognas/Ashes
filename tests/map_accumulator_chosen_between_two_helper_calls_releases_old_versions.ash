// expect: 30|1980
// A map threaded through a loop whose successor is a choice between two helper calls, neither one
// a call the map's in-place specialization takes, is placed on the reference-counted heap: each
// iteration releases the version it replaced.
import Ashes.Collection.Map
let put (k: Str) (v: Int) m = Ashes.Collection.Map.setStr(k)(v)(m)

let recursive build i n m =
    if i >= n
    then m
    else
        let m2 =
            if i - i / 2 * 2 == 0
            then
                put(Ashes.Text.fromInt(i - i / 30 * 30))(i)(m)
            else
                put(Ashes.Text.fromInt(i - i / 30 * 30))(i + 7)(m)
        in build(i + 1)(n)(m2)

let built = build(0)(2000)(Ashes.Collection.Map.empty)

let last =
    match Ashes.Collection.Map.getStr("23")(built) with
        | Some(v) -> v
        | None -> -1
in
    Ashes.IO.print(Ashes.Text.fromInt(Ashes.Collection.Map.size(built)) + "|" + Ashes.Text.fromInt(last))
