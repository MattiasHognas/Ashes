// expect: 3
type Point = { x: Int, y: Int }

let add = 
    fun (p) -> p.x + p.y
in 
    let p = Point { x = 1, y = 2 }
    in Ashes.IO.print(add(p))
