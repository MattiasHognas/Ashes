// file: chunk_input.txt = Hello, Ashes file streaming!
// expect: Hello, Ashes file streaming!
import Ashes.File
import Ashes.IO
let rec readAll handle acc = 
    match Ashes.File.readChunk(handle)(4) with
        | Error(_e) -> acc + "[err]"
        | Ok(chunk) -> 
            if chunk == ""
            then acc
            else readAll(handle)(acc + chunk)

let result = 
    match Ashes.File.open("chunk_input.txt") with
        | Error(_e) -> "open-failed"
        | Ok(handle) -> 
            let contents = readAll(handle)("")
            in 
                let _closed = Ashes.File.close(handle)
                in contents
in Ashes.IO.print(result)
