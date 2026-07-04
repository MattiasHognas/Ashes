// expect: 1=one;2=two;3=three;|3
import Ashes.Map
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

let m = Ashes.Map.set(cmp)(3)("three")(Ashes.Map.set(cmp)(1)("one")(Ashes.Map.set(cmp)(2)("two")(Ashes.Map.empty)))

let copy = Ashes.Internal.deepCopy(m)

let summary = 
    Ashes.Map.foldLeft(given (acc) -> 
        given (k) -> 
            given (v) -> acc + Ashes.Text.fromInt(k) + "=" + v + ";")("")(copy)
in Ashes.IO.print(summary + "|" + Ashes.Text.fromInt(Ashes.Map.size(copy)))
