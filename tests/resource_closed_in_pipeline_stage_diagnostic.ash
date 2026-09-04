// expect-compile-error: ASH006
// A pipeline stage that closes the file handle releases it before the next stage's closure runs, so
// the read in that closure is a use-after-close even though the closure was written first.
import Ashes.IO
import Ashes.IO.File
match Ashes.IO.File.open("input.txt") with
    | Error(_e) -> Ashes.IO.print("err")
    | Ok(fh) ->
        Unit
        |> (given (_) -> Ashes.IO.File.close(fh))
        |> (given (_) -> Ashes.IO.File.readChunk(fh)(4))
        |> (given (chunk) ->
            match chunk with
                | Error(_) -> Ashes.IO.print("read-err")
                | Ok(c) -> Ashes.IO.print(c))
