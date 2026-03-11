// expect-compile-error: Import name collision for imported binding 'z'
import M.X
import M.Y
Ashes.IO.print(z)
