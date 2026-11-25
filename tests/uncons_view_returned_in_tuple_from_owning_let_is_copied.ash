// expect: 1zzzzzzzzzzzz72zzzzzzzzzzzz14#tu5pl
// A `let` owns a call result and returns the head and tail of its uncons in a tuple: the tuple is
// copied before the string is released.
let id (text: Str) = text + ""

let inTuple (n: Int) =
    (let s = id("tu" + Ashes.Text.fromInt(n) + "pl")
    in
        match Ashes.Text.unconsText(s) with
            | Some((h, t)) -> (h, t)
            | None -> ("", ""))

let noise (n: Int) = Ashes.Text.fromInt(n) + "zzzzzzzzzzzz" + Ashes.Text.fromInt(n * 7)

let tp = inTuple(5)

Ashes.IO.print(noise(1) + noise(2) + "#" + (match tp with
    | (h, t) -> h + t))
