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
    14
    |> Callback(given (n) -> n * 3)
    |> apply
in
    Ashes.IO.print(r + Ashes.Text.byteLength("hi"
    |> Boxed(1 :: 2 :: [])
    |> describe))
