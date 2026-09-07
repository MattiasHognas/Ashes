// expect-compile-error: expects an unsigned integer
// Ashes.Number.UInt.toInt rejects a non-unsigned argument (here a plain Int) with a clear diagnostic.
import Ashes.IO
import Ashes.Text
import Ashes.Number.UInt
5
|> Ashes.Number.UInt.toInt
|> Ashes.Text.fromInt
|> Ashes.IO.print
