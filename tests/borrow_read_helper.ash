// file: borrow_in.txt = hello
// expect: llo
// A read-only helper borrows the FileHandle instead of consuming it: peek reads the first 2 bytes,
// then the caller keeps the still-open fh and reads the rest (fh is closed exactly once, by the
// caller). Before borrow inference, passing fh to peek moved it and the second read was an ASH008
// use-after-move.
import Ashes.IO.File
import Ashes.IO
let peek =
    given (h) -> Ashes.IO.File.readChunk(h)(2)

let opened = Ashes.IO.File.open("borrow_in.txt")
in
    match opened with
        | Error(_e) -> Ashes.IO.print("err")
        | Ok(fh) ->
            let head = peek(fh)
            in
                match Ashes.IO.File.readChunk(fh)(3) with
                    | Error(_) -> Ashes.IO.print("read-err")
                    | Ok(c) -> Ashes.IO.print(c)
