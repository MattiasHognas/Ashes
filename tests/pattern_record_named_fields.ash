// expect: 9
type Point =
    | x: Int
    | y: Int

let point = Point(x = 4, y = 9)
in
    match point with
        | Point { y = value } -> Ashes.IO.print(value)
