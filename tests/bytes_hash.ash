// expect: -5808556873153909620|true|false
import Ashes.Byte
import Ashes.Text
import Ashes.IO
let h s = Ashes.Byte.hash(Ashes.Byte.fromText(s))

let a = h("a")
in
    Ashes.IO.print(Ashes.Text.fromInt(a) + "|" + (if h("a") == a
    then "true"
    else "false") + "|" + (if h("b") == a
    then "true"
    else "false"))
