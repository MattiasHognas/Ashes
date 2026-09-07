// expect-compile-error: expects Int
// Ashes.Number.UInt.fromInt64 rejects a non-Int argument (here a u8) with a clear diagnostic.
import Ashes.IO
import Ashes.Text
import Ashes.Number.UInt
5u8
|> Ashes.Number.UInt.fromInt64
|> Ashes.Number.UInt.toInt
|> Ashes.Text.fromInt
|> Ashes.IO.print
