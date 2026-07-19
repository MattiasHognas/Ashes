// file: recadt_input.txt = z
// expect: z
// A user-defined ADT that nests both itself and a resource (Bag = Mt | Put(FileHandle, Bag)) is a
// self-recursive resource-bearing type. Reading the head proves it is open; the unused tail is
// dropped at scope exit by a synthesized recursive dropper that walks the whole chain at runtime.
// Before the fix a static unfold stopped at the first self-reference, leaking every tail handle to
// program exit (verified separately: peak concurrent fds 1202 -> 8 over 200 iterations).
import Ashes.IO.File
import Ashes.IO
type Bag =
    | MtBag
    | Put(FileHandle, Bag)

let recursive build n acc =
    if n <= 0
    then acc
    else
        match Ashes.IO.File.open("recadt_input.txt") with
            | Error(_e) -> acc
            | Ok(fh) -> build(n - 1)(Put(fh)(acc))

let bag = build(3)(MtBag)
in
    match bag with
        | MtBag -> Ashes.IO.print("empty")
        | Put(fh, rest) ->
            match Ashes.IO.File.readChunk(fh)(1) with
                | Error(_) -> Ashes.IO.print("read-err")
                | Ok(c) -> Ashes.IO.print(c)
