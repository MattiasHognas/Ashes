// expect-compile-error: ASH048 Constructor 'Point' is declared in module 'Points', which this module does not import.
import Plane
let moved = Point(x = 1, y = Plane.origin.y)

Ashes.IO.print(moved.x)
