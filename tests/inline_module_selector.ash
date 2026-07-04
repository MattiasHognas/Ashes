// expect: 49
import Geometry.square as sq

module Geometry =
    let square = given (x) -> x * x

Ashes.IO.print(sq(7))
