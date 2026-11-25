// expect: 1zzzzzzzzzzzz72zzzzzzzzzzzz14#b1cd
// A `let` owns a call result and binds its uncons; a later match puts the tail, a view of the
// owned string, into the record the scope returns: the record is copied before the string is
// released.
type Holder =
    | v: Str

let id (text: Str) = text + ""

let inRecord (n: Int) =
    (let s = id("ab" + Ashes.Text.fromInt(n) + "cd")
    in
        let parts = Ashes.Text.unconsText(s)
        in
            match parts with
                | Some((_h, t)) -> Holder(v = t)
                | None -> Holder(v = ""))

let noise (n: Int) = Ashes.Text.fromInt(n) + "zzzzzzzzzzzz" + Ashes.Text.fromInt(n * 7)

let r = inRecord(1)

Ashes.IO.print(noise(1) + noise(2) + "#" + r.v)
