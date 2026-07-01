// file-bytes: lines.txt = 6F 6E 65 0A 74 77 6F 0D 0A 74 68 72 65 65
// expect: one|two|three|<count=3>
import Ashes.File
import Ashes.IO
import Ashes.Text
let rec go handle acc count = 
    match Ashes.File.readLine(handle) with
        | None -> acc + "<count=" + Ashes.Text.fromInt(count) + ">"
        | Some(line) -> go(handle)(acc + line + "|")(count + 1)

let result = 
    match Ashes.File.open("lines.txt") with
        | Error(_e) -> "open-failed"
        | Ok(handle) -> 
            let text = go(handle)("")(0)
            in 
                let _closed = Ashes.File.close(handle)
                in text
in Ashes.IO.writeLine(result)
