// file: cdc_input.txt = Z
// expect: 50
// Each iteration opens a file, captures the fh in a closure that escapes the match arm, reads
// through the closure, then drops the closure at the tail-call back-edge — which runs the closure's
// resource dropper (closure+24) to close the fh deterministically.
// Reaching 50 successful reads exercises the dropper path without corruption;
// the fd-release guarantee itself is verified under a low `ulimit -n`.
import Ashes.File
import Ashes.IO
import Ashes.Text
let rec loop n acc = 
    if n <= 0
    then acc
    else 
        let reader = 
            match Ashes.File.open("cdc_input.txt") with
                | Error(_e) -> 
                    fun (x) -> 0
                | Ok(h) -> 
                    fun (x) -> 
                        match Ashes.File.readChunk(h)(1) with
                            | Error(_) -> 0
                            | Ok(_c) -> 1
        in loop(n - 1)(acc + reader(0))
in Ashes.IO.print(Ashes.Text.fromInt(loop(50)(0)))
