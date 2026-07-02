// expect-compile-error: expects an unsigned integer
// Ashes.UInt.toInt rejects a non-unsigned argument (here a plain Int) with a clear diagnostic.
import Ashes.IO
import Ashes.Text
import Ashes.UInt
Ashes.IO.print(Ashes.Text.fromInt(Ashes.UInt.toInt(5)))
