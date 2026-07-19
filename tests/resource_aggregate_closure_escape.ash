// file: agg_closure_input.txt = hello
// expect: hello
// A resource captured by a closure that escapes nested inside an aggregate (Some(closure)) must stay
// open. The resource is invisible in the aggregate's type (Maybe(Unit -> Str)), so the escape is
// detected from the closure capture, not the type. Before the fix the fd was closed early and this
// read "read-err".
import Ashes.IO.File
import Ashes.Core.Maybe
import Ashes.IO
let boxed =
    match Ashes.IO.File.open("agg_closure_input.txt") with
        | Error(_e) -> None
        | Ok(fh) ->
            Some(given (x) ->
                match Ashes.IO.File.readChunk(fh)(5) with
                    | Error(_) -> "read-err"
                    | Ok(chunk) -> chunk)
in
    match boxed with
        | None -> Ashes.IO.print("none")
        | Some(f) -> Ashes.IO.print(f(0))
