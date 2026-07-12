// expect: 5
type Point =
    | x: Int
    | y: Int

let p = Point(x = 1, y = 2)
in
    let p2 = p with x = 5
    in Ashes.IO.print(p2.x)
