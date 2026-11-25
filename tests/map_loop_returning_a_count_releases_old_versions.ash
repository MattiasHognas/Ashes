// expect: 3000|25
// A map threaded through a loop that returns a count rather than the map is placed on the
// reference-counted heap all the same: the count keeps nothing of the map, so each iteration
// releases the version a call in one branch replaced.
import Ashes.Collection.Map
let recursive count i n m total =
    if i >= n
    then total * 1000 + Ashes.Collection.Map.size(m)
    else
        let m2 =
            if i - i / 2 * 2 == 0
            then
                Ashes.Collection.Map.setStr(Ashes.Text.fromInt(i - i / 50 * 50))(i)(m)
            else m
        in count(i + 1)(n)(m2)(total + 1)

let result = count(0)(3000)(Ashes.Collection.Map.empty)(0)
in Ashes.IO.print(Ashes.Text.fromInt(result / 1000) + "|" + Ashes.Text.fromInt(result - result / 1000 * 1000))
