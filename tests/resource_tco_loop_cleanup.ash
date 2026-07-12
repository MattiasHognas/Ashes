// file: loop_input.txt = abc
// expect: abcabcabc
// Opens and reads the same file on each iteration of a tail-recursive loop, never closing it
// explicitly. Each iteration's FileHandle must be (a) usable for the read inside the arm, then
// (b) closed at the tail-call back-edge so fds don't leak.
// Reading the file 3x and getting its content back proves the fh is valid during use and that
// the loop runs in bounded fds.
import Ashes.File
import Ashes.IO
let recursive loop n acc =
    if n <= 0
    then acc
    else
        match Ashes.File.open("loop_input.txt") with
            | Error(_e) -> acc + "[err]"
            | Ok(fh) ->
                match Ashes.File.readChunk(fh)(3) with
                    | Error(_) -> acc + "[rerr]"
                    | Ok(chunk) -> loop(n - 1)(acc + chunk)
in Ashes.IO.print(loop(3)(""))
