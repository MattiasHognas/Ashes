// file: chunk_input.txt = Hello, Ashes file streaming!
// expect: Hello, Ashes file streaming!
import Ashes.File
import Ashes.IO
let rec readAll fh acc = 
    match Ashes.File.readChunk(fh)(4) with
        | Error(_e) -> acc + "[err]"
        | Ok(chunk) -> 
            if chunk == ""
            then acc
            else readAll(fh)(acc + chunk)

let result = 
    match Ashes.File.open("chunk_input.txt") with
        | Error(_e) -> "open-failed"
        | Ok(fh) -> 
            let contents = readAll(fh)("")
            in 
                let _closed = Ashes.File.close(fh)
                in contents
in Ashes.IO.print(result)
