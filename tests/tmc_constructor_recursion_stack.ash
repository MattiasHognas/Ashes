// The stack-depth claim: a `head :: self(tail)` producer runs in constant native stack, so a list far
// longer than the old one-frame-per-element limit completes instead of faulting. Before the
// tail-modulo-constructor transform this shape cost roughly 209 bytes of stack per element, so 400000
// elements needed about 80 MiB and died on an ordinary 8 MiB stack; the produced list is summed here
// so the spine is really built and really consumed, not optimized away.
// expect: 80000600000
import Ashes.IO as io
import Ashes.Text as text
let recursive build count acc =
    if count == 0
    then acc
    else build(count - 1)(count :: acc)

let recursive incAll xs =
    match xs with
        | [] -> []
        | head :: tail -> head + 1 :: incAll(tail)

let recursive sum xs acc =
    match xs with
        | [] -> acc
        | head :: tail -> sum(tail)(acc + head)

0
|> sum([]
|> build(400000)
|> incAll)
|> text.fromInt
|> io.print
