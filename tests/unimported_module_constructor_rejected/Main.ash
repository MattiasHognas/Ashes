// expect-compile-error: ASH048 Constructor 'Circle' is declared in module 'Shapes', which this module does not import.
import Geometry
Ashes.IO.print(match Geometry.unit with
    | Circle(r) -> r
    | _ -> 0)
