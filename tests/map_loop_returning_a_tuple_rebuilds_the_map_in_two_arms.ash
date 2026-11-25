// expect: 10100
// A map rebuilt in either arm of an `if` through a loop that returns it in a tuple: a call routed
// to the in-place specialization hands its result on as the rewritten accumulator, never into a
// call-result slot only the function exit releases.
import Ashes.Collection.Map
let recursive build i n m =
    if i >= n
    then (m, i)
    else
        let m2 =
            if i - i / 2 * 2 == 0
            then
                Ashes.Collection.Map.setStr(Ashes.Text.fromInt(i - i / 10 * 10))(i)(m)
            else
                Ashes.Collection.Map.setStr(Ashes.Text.fromInt(i - i / 10 * 10))(i + 7)(m)
        in build(i + 1)(n)(m2)

let result =
    match build(0)(100)(Ashes.Collection.Map.empty) with
        | (built, count) -> Ashes.Collection.Map.size(built) * 1000 + count
in
    result
    |> Ashes.Text.fromInt
    |> Ashes.IO.print
