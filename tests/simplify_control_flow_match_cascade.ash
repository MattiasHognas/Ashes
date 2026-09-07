// expect: 10
type Shape =
    | Circle(Int)
    | Square(Int)
    | Rectangle(Int, Int)
    | Triangle(Int, Int)

let area s =
    match s with
        | Circle(r) -> r * r * 3
        | Square(side) -> side * side
        | Rectangle(w, h) -> w * h
        | Triangle(b, h) -> b * h / 2

let result =
    5
    |> Rectangle(2)
    |> area
in Ashes.IO.print(result)
