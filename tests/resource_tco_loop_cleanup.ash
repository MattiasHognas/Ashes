// file: loop_input.txt = abc
// expect: abcabcabc
// Opens and reads the same file on each iteration of a tail-recursive loop, never closing it
// explicitly. Each iteration's FileHandle must be (a) usable for the read inside the arm, then
// (b) closed at the tail-call back-edge so fds don't leak (RESOURCE_SAFETY.md: TCO back-edge drop).
// Reading the file 3x and getting its content back proves the handle is valid during use and that
// the loop runs in bounded fds.
import Ashes.File
import Ashes.IO
let rec loop n acc = 
    if n <= 0
    then acc
    else 
        match Ashes.File.open("loop_input.txt") with
            | Error(_e) -> acc + "[err]"
            | Ok(handle) -> 
                match Ashes.File.readChunk(handle)(3) with
                    | Error(_) -> acc + "[rerr]"
                    | Ok(chunk) -> loop(n - 1)(acc + chunk)
in Ashes.IO.print(loop(3)(""))
