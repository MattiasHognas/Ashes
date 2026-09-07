// A read-only builtin consuming a fresh reference-counted call result releases it: straight from the
// call, and through an if join whose every branch produced a fresh value. A join with one borrowed
// branch (the loop's own parameter) stays owned by that binding, and a let-bound result stays owned
// by its scope, whose exit release covers it. Each shape runs for 20000 iterations so a double
// release shows up as a crash or a wrong total; the C# memory-plateau test checks the leak itself.
// expect: 5689216|4266976|7964736|11378432
let recursive widen (n: Int) (text: Str) =
    if n == 0
    then text
    else widen(n - 1)(text + text)

let recursive direct (n: Int) (total: Int) =
    if n == 0
    then total
    else
        direct(n - 1)(total + Ashes.Text.byteLength(n
        |> Ashes.Text.fromInt
        |> widen(6)))

let recursive joined (n: Int) (total: Int) =
    if n == 0
    then total
    else
        joined(n - 1)(total + Ashes.Text.byteLength(if n % 2 == 0
        then
            n
            |> Ashes.Text.fromInt
            |> widen(6)
        else
            n
            |> Ashes.Text.fromInt
            |> widen(5)))

let recursive mixed (n: Int) (text: Str) (total: Int) =
    if n == 0
    then total
    else
        mixed(n - 1)(text)(total + Ashes.Text.byteLength(if n % 2 == 0
        then
            n
            |> Ashes.Text.fromInt
            |> widen(6)
        else text))

let recursive scoped (n: Int) (total: Int) =
    if n == 0
    then total
    else
        let wide =
            n
            |> Ashes.Text.fromInt
            |> widen(6)
        in scoped(n - 1)(total + Ashes.Text.byteLength(wide) + Ashes.Text.byteLength(wide))

Ashes.IO.print(Ashes.Text.fromInt(direct(20000)(0)) + "|" + Ashes.Text.fromInt(joined(20000)(0)) + "|" + Ashes.Text.fromInt(mixed(20000)(widen(8)("ab"))(0)) + "|" + Ashes.Text.fromInt(scoped(20000)(0)))
