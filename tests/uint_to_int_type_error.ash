// expect-compile-error: expects an unsigned integer
// Ashes.Number.UInt.toInt rejects a non-unsigned argument (here a plain Int) with a clear diagnostic.
import Ashes.IO
import Ashes.Text
import Ashes.Number.UInt
Ashes.IO.print(Ashes.Text.fromInt(Ashes.Number.UInt.toInt(5)))
