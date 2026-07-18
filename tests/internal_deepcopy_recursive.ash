// expect: 1=one;2=two;3=three;|3
import Ashes.Collection.Map
import Ashes.Internal
import Ashes.Text
import Ashes.IO
let cmp a b =
    if a == b
    then 0
    else
        if a <= b
        then -1
        else 1

let m = Ashes.Collection.Map.set(cmp)(3)("three")(Ashes.Collection.Map.set(cmp)(1)("one")(Ashes.Collection.Map.set(cmp)(2)("two")(Ashes.Collection.Map.empty)))

let copy = Ashes.Internal.deepCopy(m)

let summary =
    Ashes.Collection.Map.foldLeft(given (acc) ->
        given (k) ->
            given (v) -> acc + Ashes.Text.fromInt(k) + "=" + v + ";")("")(copy)
in Ashes.IO.print(summary + "|" + Ashes.Text.fromInt(Ashes.Collection.Map.size(copy)))
