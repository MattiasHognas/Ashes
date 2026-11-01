// expect: 12
import Geometry
import Geometry.cube as cubed

module Geometry =
    let square = given (x) -> x * x
    let cube = given (x) -> x * square(x)

Ashes.IO.print(square(2) + cubed(2))
