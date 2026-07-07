// expect: 44
import Ashes.IO
import Ashes.Text
type Callback =
    | Callback(Int -> Int, Int)

type Boxed =
    | Boxed(List(Int), Str)

let apply cb = 
    match cb with
        | Callback(f, x) -> f(x)

let describe b = 
    match b with
        | Boxed(_xs, label) -> label

let r = 
    apply(Callback(given (n) -> n * 3)(14))
in Ashes.IO.print(r + Ashes.Text.byteLength(describe(Boxed(1 :: 2 :: [])("hi"))))
