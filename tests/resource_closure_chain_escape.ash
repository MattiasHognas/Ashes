// file: closure_chain_input.txt = hello
// expect: hello
// A resource captured by an inner closure that is reachable only through an OUTER escaping closure
// must stay open for the escaped value. The arm scope transfers ownership to the closure chain rather
// than closing the fh. Before the fix this second-order escape closed the fd and read "read-err".
import Ashes.IO.File
import Ashes.IO
let outer =
    match Ashes.IO.File.open("closure_chain_input.txt") with
        | Error(_e) ->
            given (x) -> "no-file"
        | Ok(fh) ->
            let inner =
                given (x) ->
                    match Ashes.IO.File.readChunk(fh)(5) with
                        | Error(_) -> "read-err"
                        | Ok(chunk) -> chunk
            in
                given (x) -> inner(x)
in Ashes.IO.print(outer(0))
