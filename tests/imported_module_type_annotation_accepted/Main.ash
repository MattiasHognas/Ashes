// expect: 3
import Geometry
import Shapes.Shape
let describe (shape: Shape) = Shapes.area(shape)

Geometry.unit
|> describe
|> Ashes.IO.print
