// expect: 13|104|33
// file: rab.txt = hello, bytes!
// Ashes.IO.File.readAllBytes reads a whole file into a Bytes with no UTF-8 validation (uncapped on Linux).
// Checks the length and two bytes: 'h' (104) at index 0 and '!' (33) at index 12.
import Ashes.IO
import Ashes.IO.File
import Ashes.Text
import Ashes.Byte
import Ashes.Number.UInt
match Ashes.IO.File.readAllBytes("rab.txt") with
    | Error(_e) -> Ashes.IO.print("err")
    | Ok(b) -> Ashes.IO.print(Ashes.Text.fromInt(Ashes.Byte.length(b)) + "|" + Ashes.Text.fromInt(Ashes.Number.UInt.toInt(Ashes.Byte.get(b)(0))) + "|" + Ashes.Text.fromInt(Ashes.Number.UInt.toInt(Ashes.Byte.get(b)(12))))
