// expect-compile-error: ASH008
// A helper that CLOSES the resource consumes it — it is not a borrow. Passing fh to it moves
// ownership, so using fh afterwards is a use-after-move, even with borrow inference. This guards the
// soundness boundary: only pure-read helpers borrow; anything that closes/stores/returns still moves.
import Ashes.IO.File
import Ashes.IO
let closeIt =
    given (h) -> Ashes.IO.File.close(h)
in
    match Ashes.IO.File.open("input.txt") with
        | Error(_e) -> Ashes.IO.print("err")
        | Ok(fh) ->
            let done = closeIt(fh)
            in
                match Ashes.IO.File.readChunk(fh)(4) with
                    | Error(_) -> Ashes.IO.print("read-err")
                    | Ok(c) -> Ashes.IO.print(c)
