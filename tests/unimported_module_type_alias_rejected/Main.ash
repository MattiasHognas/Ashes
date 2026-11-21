// expect-compile-error: ASH048 Type 'Distance' is declared in module 'Units', which this module does not import.
import Geometry
let distance (value: Distance) = value

Geometry.origin
|> distance
|> Ashes.IO.print
