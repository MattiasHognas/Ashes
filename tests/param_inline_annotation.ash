// Regression: inline parameter type annotations. `given (x: Type) -> ...` and the parenthesized
// annotated sugar parameter `let f (b: Body) = ...` both unify the parameter with the annotated
// type before the body is lowered, so record dot-access on the parameter and Float operator
// selection resolve without annotating the whole binding. Also covers the stitched-module path
// (the Ashes.List import forces the paren-wrapped flat entry block, whose text-based header
// scanners must accept the parenthesized annotated parameter).
// expect: e=6.0 10.0 3
import Ashes.IO as io
import Ashes.Text as text
import Ashes.Math as math
import Ashes.List as list
type Body =
    | mass: Float
    | vx: Float

let energy (b: Body) = b.mass * b.vx

let scale =
    given (v: Float) ->
        given (n: Int) -> v * math.toFloat(n)

let describe (b: Body) label = label + text.formatFloat(energy(b))(1)

let lenOf (xs: List(Int)) = list.length(xs)

io.print(describe(Body(mass = 3.0, vx = 2.0))("e=") + " " + text.formatFloat(scale(2.5)(4))(1) + " " + text.fromInt(lenOf([1, 2, 3])))
