// expect: Some:5
import Ashes.String
import Ashes.IO
type Box =
    | Full(Int)
    | Empty

let unbox b = 
    match b with
        | Full(v) -> "Some:" + Ashes.Text.fromInt(v)
        | Empty -> "None"
in Ashes.IO.print(unbox(Full(Ashes.String.length("hello"))))
