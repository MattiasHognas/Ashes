// expect: 8
type Choice =
    | First(Int)
    | Second(Int)
    | Empty

let choice = Second(8)
in
    match choice with
        | First(value) | Second(value) when value > 0 -> Ashes.IO.print(value)
        | First(_) | Second(_) | Empty -> Ashes.IO.print(0)
