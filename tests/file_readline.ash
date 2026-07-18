// file-bytes: lines.txt = 6F 6E 65 0A 74 77 6F 0D 0A 74 68 72 65 65
// expect: one|two|three|<count=3>
import Ashes.IO.File
import Ashes.IO
import Ashes.Text
let recursive go fh acc count =
    match Ashes.IO.File.readLine(fh) with
        | None -> acc + "<count=" + Ashes.Text.fromInt(count) + ">"
        | Some(line) -> go(fh)(acc + line + "|")(count + 1)

let result =
    match Ashes.IO.File.open("lines.txt") with
        | Error(_e) -> "open-failed"
        | Ok(fh) ->
            let text = go(fh)("")(0)
            in
                let _closed = Ashes.IO.File.close(fh)
                in text
in Ashes.IO.writeLine(result)
