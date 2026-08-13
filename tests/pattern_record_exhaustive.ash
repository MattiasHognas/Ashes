// expect: 4
type Point =
    | x: Int
    | y: Int

let point = Point(x = 4, y = 9)
in
    match point with
        | Point { x = value } -> Ashes.IO.print(value)
