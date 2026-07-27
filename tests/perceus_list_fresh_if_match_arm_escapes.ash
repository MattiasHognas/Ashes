// expect: 2000
// Perceus unification Phase 5: a fresh list literal returned from an if/match arm is now recognized
// as an escaping runtime-managed (RC) result, not only when the whole let/lambda body IS the list
// construction directly. Looped many times so a wrong classification (either a leak from staying
// arena when it should have been promoted, or corruption from a false-positive RC promotion) would
// show up as a wrong total or a crash, not just a silent no-op.
import Ashes.Text as text
import Ashes.IO as io
let build flag =
    (let discard = 0
    in
        if flag
        then [text.fromInt(40), text.fromInt(2)]
        else [text.fromInt(0)])

let recursive sumLengths values acc =
    match values with
        | [] -> acc
        | head :: rest -> sumLengths(rest)(acc + text.byteLength(head))

let recursive repeat n acc =
    if n == 0
    then acc
    else repeat(n - 1)(acc + sumLengths(build(n % 2 == 0))(0))

io.print(text.fromInt(repeat(1000)(0)))
