// expect: 206977795
// A handler arm that resumes with its own parameter returns the arm's reference-counted copy of
// that parameter. The perform site reads the arm closure's returns bit and adopts the value, the
// return arm applies to it as a continuation, and the caller releases it, so the resident set
// plateaus over two hundred thousand calls instead of growing by one copy per call.

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
        let kept = decorate(fill(64)(Ashes.Text.fromInt(n)))
        in
            let scratch = fill(64)(Ashes.Text.fromInt(n + 1))
            in loop(n - 1)(total + Ashes.Text.byteLength(kept) + Ashes.Text.byteLength(scratch))

Ashes.IO.print(Ashes.Text.fromInt(loop(200000)(0)))
