// expect: 1
type Point = { x: Int, y: Int }

let p = Point { x = 1, y = 2 }
in Ashes.IO.print(p.x)
