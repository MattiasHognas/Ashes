module Geometry =
    export (value square, value cube, type Unit2(..), module Inner)
    type Unit2 =
        | Meter
        | Foot
    let square = given (x) -> x * x
    let cube = given (x) -> x * square(x)
    module Inner =
        let half = given (x) -> x / 2

let viaQualified = Geometry.square(3)
let viaNested = Geometry.Inner.half(10)
let viaSelector = Geometry.cube(2)
let unit = Geometry.Meter

Ashes.IO.print(viaQualified + viaNested + viaSelector)
