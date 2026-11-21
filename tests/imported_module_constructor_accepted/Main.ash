// expect: 3
import Geometry
import Shapes
let radius shape =
    match shape with
        | Circle(r) -> r
        | _ -> 0

Ashes.IO.print(radius(Geometry.unit) + radius(Circle(2)))
