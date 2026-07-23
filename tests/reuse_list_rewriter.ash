// A recursive list rewriter applied to a uniquely-owned loop accumulator should overwrite the
// untagged cons cells in place. A fresh cell per turn would make this constant-sized loop grow.
// expect: 300006
import Ashes.IO as io
import Ashes.Text as text
let recursive bumpAll values =
    match values with
        | [] -> []
        | value :: rest -> value + 1 :: bumpAll(rest)

let recursive repeat turns values =
    if turns == 0
    then values
    else repeat(turns - 1)(bumpAll(values))

let recursive sum values total =
    match values with
        | [] -> total
        | value :: rest -> sum(rest)(total + value)

io.print(text.fromInt(sum(repeat(100000)([1, 2, 3]))(0)))
