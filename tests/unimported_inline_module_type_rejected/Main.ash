// expect-compile-error: ASH048 Type 'Shape' is declared in module 'Shapes.Inner', which this module does not import.
import Geometry
let describe (shape: Shape) = 1

Geometry.unit
|> describe
|> Ashes.IO.print
