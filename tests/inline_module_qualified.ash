// expect: 12
module Geometry =
    let pi = 3
    let area = given (r) -> pi * r * r

Ashes.IO.print(Geometry.area(2))
