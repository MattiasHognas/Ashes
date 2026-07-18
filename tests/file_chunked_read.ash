// file: chunk_input.txt = Hello, Ashes file streaming!
// expect: Hello, Ashes file streaming!
import Ashes.IO.File
import Ashes.IO
let recursive readAll fh acc =
    match Ashes.IO.File.readChunk(fh)(4) with
        | Error(_e) -> acc + "[err]"
        | Ok(chunk) ->
            if chunk == ""
            then acc
            else readAll(fh)(acc + chunk)

let result =
    match Ashes.IO.File.open("chunk_input.txt") with
        | Error(_e) -> "open-failed"
        | Ok(fh) ->
            let contents = readAll(fh)("")
            in
                let _closed = Ashes.IO.File.close(fh)
                in contents
in Ashes.IO.print(result)
