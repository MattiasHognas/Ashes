// expect: 2
type Point =
    | x: Int
    | y: Int

let describe p =
    (let x = p.x
    in
        let y = p.x
        in x + y)

let result = describe(Point(x = 1, y = 2))
in Ashes.IO.print(result)
