// expect: Some:5
import Ashes.Text
import Ashes.IO
type Box =
    | Full(Int)
    | Empty

let unbox b =
    match b with
        | Full(v) -> "Some:" + Ashes.Text.fromInt(v)
        | Empty -> "None"
in
    Full(Ashes.Text.length("hello"))
    |> unbox
    |> Ashes.IO.print
