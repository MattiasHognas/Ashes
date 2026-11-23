type Shape =
    | Circle(Int)
    | Square(Int)

let area (shape: Shape) =
    match shape with
        | Circle(r) -> 3 * r * r
        | Square(s) -> s * s
