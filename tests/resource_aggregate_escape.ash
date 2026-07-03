// file: agg_escape_input.txt = hello
// expect: hello
// A FileHandle wrapped in Some(...) and returned from a match arm must stay open for the value's
// user: storing a resource into an aggregate moves ownership into it, so the arm must not close the
// fh (the aggregate analog of the escaping-closure case). Before the fix
// the escaped Some(fh) held a closed fd and this read "read-err".
import Ashes.File
import Ashes.Maybe
import Ashes.IO
let wrapped = 
    match Ashes.File.open("agg_escape_input.txt") with
        | Error(_e) -> None
        | Ok(h) -> Some(h)

let result = 
    match wrapped with
        | None -> "none"
        | Some(h2) -> 
            match Ashes.File.readChunk(h2)(5) with
                | Error(_) -> "read-err"
                | Ok(chunk) -> chunk
in Ashes.IO.print(result)
