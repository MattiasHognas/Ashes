// expect: 12
type Point =
    | x: Int
    | y: Int

let perimeter p = 2 * (p.x + p.y)

let r = Point(x = 1, y = 2)

let area = perimeter(r) + perimeter(r)
in Ashes.IO.print(area)
