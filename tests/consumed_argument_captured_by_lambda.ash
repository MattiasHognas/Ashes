// expect: 41337792
// A fresh string consumed by a callee whose record result holds a closure capturing it: the
// callee's result reach is unknown through the lambda, and the argument is neither released
// while the closure can still read it nor read back from reused memory. The scratch string
// allocated after each call would take over a freed cell.

type Box =
    | reader: Int -> Str

let box (s: Str) =
    Box(reader = given (u: Int) -> s)

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
            |> box
        in
            let scratch =
                n + 1
                |> Ashes.Text.fromInt
                |> fill(64)
            in
                match kept with
                    | Box { reader = reader } ->
                        loop(n - 1)(total + Ashes.Text.byteLength(reader(0)) + Ashes.Text.byteLength(scratch))

0
|> loop(40000)
|> Ashes.Text.fromInt
|> Ashes.IO.print
