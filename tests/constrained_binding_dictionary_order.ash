// expect: inherited dictionary layout passed
let recursive increasing left right =
    if left < right
    then left != right
    else false

let integers = increasing(1)(2) && increasing(2)(1) == false && increasing(1)(1) == false

let strings = increasing("a")("b") && increasing("b")("a") == false && increasing("a")("a") == false

if integers && strings
then Ashes.IO.print("inherited dictionary layout passed")
else Ashes.IO.panic("Ord methods and inherited Eq used the wrong fields")
