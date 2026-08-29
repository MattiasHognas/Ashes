// expect-compile-error: expects Int
// Ashes.Number.UInt.fromInt64 rejects a non-Int argument (here a u8) with a clear diagnostic.
import Ashes.IO
import Ashes.Text
import Ashes.Number.UInt
Ashes.IO.print(Ashes.Text.fromInt(Ashes.Number.UInt.toInt(Ashes.Number.UInt.fromInt64(5u8))))
