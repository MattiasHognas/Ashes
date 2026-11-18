// A record literal that gives a field twice is rejected.
// expect-compile-error: Field 'x' is provided more than once in record literal for 'Point'.
type Point =
    | x: Int
    | y: Int

let point = Point(x = 1, x = 2, y = 3)

Ashes.IO.print(point.y)
