// file: escape_input.txt = line1-content
// expect: line1
// A closure that captures an open FileHandle and escapes its match arm must observe a still-open
// fh: the arm's scope must NOT close a resource the escaping closure still needs.
// Before the fix this read a closed fd and returned "read-err".
import Ashes.File
import Ashes.IO
let reader = 
    match Ashes.File.open("escape_input.txt") with
        | Error(_e) -> 
            given (x) -> "no-file"
        | Ok(fh) -> 
            given (x) -> 
                match Ashes.File.readChunk(fh)(5) with
                    | Error(_) -> "read-err"
                    | Ok(chunk) -> chunk
in Ashes.IO.print(reader(0))
