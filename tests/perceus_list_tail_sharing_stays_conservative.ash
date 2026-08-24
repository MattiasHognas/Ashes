// expect: 125250000
// Perceus unification Phase 5 tail-sharing guard: a cons cell built onto an
// EXISTING list's own tail (`h :: t`, or a tail produced by a recursive call) must never be promoted
// to RC just because control-flow-transparent freshness now sees through if/match arms. Run at real
// scale (1000 outer iterations x 500-element lists): a false-positive RC promotion here would either
// corrupt the shared tail (wrong sum) or crash; a regression in cost would show up as a large slowdown.
import Ashes.Text as text
import Ashes.IO as io
let recursive build n acc =
    if n == 0
    then acc
    else build(n - 1)(n :: acc)

let recursive passThroughHead xs =
    match xs with
        | [] -> []
        | head :: tail -> head :: tail

let recursive sum values acc =
    match values with
        | [] -> acc
        | head :: rest -> sum(rest)(acc + head)

let recursive repeat n total =
    if n == 0
    then total
    else repeat(n - 1)(total + sum(passThroughHead(build(500)([])))(0))

io.print(text.fromInt(repeat(1000)(0)))
