// expect: 11
type Point =
    | x: Int
    | y: Int

let p = Point(x = 1, y = 2)
in 
    let q = p with x = 5 with y = 6
    in Ashes.IO.print(q.x + q.y)
