// expect: 1zzzzzzzzzzzz72zzzzzzzzzzzz14#l2os
// A `let` owns a call result and returns a closure capturing the tail of its uncons, a view of the
// owned string: the string is kept alive for the closure.
let id (text: Str) = text + ""

let inClosure (n: Int) =
    (let s = id("cl" + Ashes.Text.fromInt(n) + "os")
    in
        match Ashes.Text.unconsText(s) with
            | Some((_h, t)) ->
                given (_u: Int) -> t
            | None ->
                given (_u: Int) -> "")

let noise (n: Int) = Ashes.Text.fromInt(n) + "zzzzzzzzzzzz" + Ashes.Text.fromInt(n * 7)

let c = inClosure(2)

Ashes.IO.print(noise(1) + noise(2) + "#" + c(0))
