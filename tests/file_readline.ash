// file-bytes: lines.txt = 6F 6E 65 0A 74 77 6F 0D 0A 74 68 72 65 65
// expect: one|two|three|<count=3>
import Ashes.File
import Ashes.IO
import Ashes.Text
let rec go fh acc count = 
    match Ashes.File.readLine(fh) with
        | None -> acc + "<count=" + Ashes.Text.fromInt(count) + ">"
        | Some(line) -> go(fh)(acc + line + "|")(count + 1)

let result = 
    match Ashes.File.open("lines.txt") with
        | Error(_e) -> "open-failed"
        | Ok(fh) -> 
            let text = go(fh)("")(0)
            in 
                let _closed = Ashes.File.close(fh)
                in text
in Ashes.IO.writeLine(result)
