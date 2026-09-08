// expect: ok
// A handler arm that resumes with a fresh constructor application wrapping its own entry-normalized
// parameter (`resume(Just(text))`) places that result on the reference-counted heap the same way an
// ordinary function body would (OPT-51's placement half), and the perform site that dispatches to it
// requests the arena form of that result so the arm's own deep-copy-and-release epilogue reclaims the
// reference-counted original instead of leaking it (OPT-51's perform-site half). The resident set
// plateaus over two hundred thousand calls instead of growing by one string per call.

type Wrapped =
    | Just(Str)

capability Tag =
    | tag : Str -> Wrapped

let decorate (s: Str) =
    handle Tag.tag(s) with
        | Tag.tag(text) -> resume(Just(text))
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
            match kept with
                | Just(text) -> loop(n - 1)(total + Ashes.Text.byteLength(text))

0
|> loop(200000)
|> (given (_) -> Ashes.IO.print("ok"))
