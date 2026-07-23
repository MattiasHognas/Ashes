// expect: 6000
import Ashes.Collection.List as list
import Ashes.IO as io
import Ashes.Text as text
let recursive prepend n acc =
    if n == 0
    then acc
    else prepend(n - 1)("x" :: acc)

let recursive repeat n acc =
    if n == 0
    then acc
    else repeat(n - 1)(prepend(60)(acc))
in io.print(text.fromInt(list.length(repeat(100)([]))))
