// expect: 12
module Geometry =  // a trailing comment on the header is allowed
    let pi = 3
    let area = given (r) -> pi * r * r

Ashes.IO.print(Geometry.area(2))
