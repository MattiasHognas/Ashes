// expect: 0 -1 1
import Ashes.IO
import Ashes.Text
import Ashes.Trait
let cmp left right =
    if left == right
    then 0
    else
        if left <= right
        then -1
        else 1
in Ashes.IO.print(Ashes.Text.fromInt(cmp(1)(1)) + " " + Ashes.Text.fromInt(cmp(1)(2)) + " " + Ashes.Text.fromInt(cmp(2)(1)))
