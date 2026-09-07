// expect: 41337792
// A fresh string consumed by a callee whose result flows through a result pipe: the callee's
// result reach is unknown through the pipe, and the argument is neither released while the
// result still holds it nor read back from reused memory. The scratch string allocated after
// each call would take over a freed cell.

let check (s: Str) =
    if Ashes.Text.byteLength(s) > 0
    then Ok(s)
    else Error("empty")

let stamp (s: Str) =
    match s
    |> check
    |?> (given (text) -> text) with
        | Ok(text) -> text
        | Error(message) -> message

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
            |> stamp
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
