// expect: argument requirements passed
let recursive apply callback value =
    if value == value
    then callback(value)
    else callback(value)

// Ord is required by this argument lambda, while apply itself only requires Eq.
let recursive before pivot value =
    apply(given (item) -> item < pivot)(value)

let integers = before(2)(1) && before(1)(2) == false

let strings = before("b")("a") && before("a")("b") == false

if integers && strings
then Ashes.IO.print("argument requirements passed")
else Ashes.IO.panic("a nested argument requirement was discarded")
