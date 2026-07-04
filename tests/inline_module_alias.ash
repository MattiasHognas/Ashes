// expect: 36
import Geometry as G

module Geometry =
    let square = given (x) -> x * x

Ashes.IO.print(G.square(6))
