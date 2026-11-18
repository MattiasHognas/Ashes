// A record pattern that names a field twice is rejected.
// expect-compile-error: Duplicate field 'x' in record pattern for 'Point'.
type Point =
    | x: Int
    | y: Int

let point = Point(x = 1, y = 3)

match point with
    | Point { x = a, x = b } -> Ashes.IO.print(a + b)
