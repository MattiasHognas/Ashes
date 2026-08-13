// expect-compile-error: Record type 'Point' has no field 'z'.
type Point =
    | x: Int
    | y: Int

match Point(x = 1, y = 2) with
    | Point { z = value } -> value
