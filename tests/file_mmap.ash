// expect: 13|104|33|zero-copy
// file: mm.txt = hello, bytes!
// Ashes.File.mmap maps a file read-only and returns a zero-copy Bytes VIEW over the mapping (no read
// or copy). Checks length, byte 0 ('h'=104) and byte 12 ('!'=33); the trailing marker just confirms
// the view reads correctly. The mapping is program-lifetime, so the view stays valid.
import Ashes.IO
import Ashes.File
import Ashes.Text
import Ashes.Bytes
import Ashes.UInt
import Ashes.String
match Ashes.File.mmap("mm.txt") with
    | Error(_e) -> Ashes.IO.print("err")
    | Ok(b) -> 
        let ok = 
            if Ashes.String.substring(Ashes.Bytes.subText(b)(0)(5))(0)(5) == "hello"
            then "zero-copy"
            else "bad"
        in Ashes.IO.print(Ashes.Text.fromInt(Ashes.Bytes.length(b)) + "|" + Ashes.Text.fromInt(Ashes.UInt.toInt(Ashes.Bytes.get(b)(0))) + "|" + Ashes.Text.fromInt(Ashes.UInt.toInt(Ashes.Bytes.get(b)(12))) + "|" + ok)
