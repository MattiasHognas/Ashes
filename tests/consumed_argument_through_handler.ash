// expect: 41337792
// A fresh string consumed by a callee whose result passes through a handler arm: the callee's
// result reach is unknown through the handle, and the argument is neither released while the
// result still holds it nor read back from reused memory. The scratch string allocated after
// each call would take over a freed cell.

capability Tag =
    | tag : Str -> Str

let decorate (s: Str) =
    handle Tag.tag(s) with
        | Tag.tag(text) -> resume(text)
        | return(r) -> r

let recursive fill (n: Int) (acc: Str) =
    if n == 0
    then acc
    else fill(n - 1)(acc + "abcdefgh")

let recursive loop (n: Int) (total: Int) =
    if n == 0
    then total
    else
        let kept =
            n
            |> Ashes.Text.fromInt
            |> fill(64)
            |> decorate
        in
            let scratch =
                n + 1
                |> Ashes.Text.fromInt
                |> fill(64)
            in loop(n - 1)(total + Ashes.Text.byteLength(kept) + Ashes.Text.byteLength(scratch))

0
|> loop(40000)
|> Ashes.Text.fromInt
|> Ashes.IO.print
