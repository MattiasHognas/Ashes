// expect: 13|104|33
// file: rab.txt = hello, bytes!
// Ashes.File.readAllBytes reads a whole file into a Bytes with no UTF-8 validation (uncapped on Linux).
// Checks the length and two bytes: 'h' (104) at index 0 and '!' (33) at index 12.
import Ashes.IO
import Ashes.File
import Ashes.Text
import Ashes.Bytes
import Ashes.UInt
match Ashes.File.readAllBytes("rab.txt") with
    | Error(_e) -> Ashes.IO.print("err")
    | Ok(b) -> Ashes.IO.print(Ashes.Text.fromInt(Ashes.Bytes.length(b)) + "|" + Ashes.Text.fromInt(Ashes.UInt.toInt(Ashes.Bytes.get(b)(0))) + "|" + Ashes.Text.fromInt(Ashes.UInt.toInt(Ashes.Bytes.get(b)(12))))
