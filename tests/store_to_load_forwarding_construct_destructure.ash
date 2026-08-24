// expect: 11
type Point =
    | x: Int
    | y: Int

let describe n =
    (let p = Point(x = n, y = n + 1)
    in
        let a = p.x
        in
            let b = p.y
            in a + b)

let result = describe(5)
in Ashes.IO.print(result)
