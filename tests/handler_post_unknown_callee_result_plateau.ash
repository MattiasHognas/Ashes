// expect: ok
// A function that calls an unknown closure parameter inside `handle ... with` (so it may execute
// under a live handler post) reads that callee's returns bit and adopts a reference-counted
// result the same way a perform site does (OPT-49a), instead of copying it at the arena call
// boundary and leaking the callee's original. The resident set plateaus over two hundred
// thousand calls instead of growing by one copy per call.

capability Tag =
    | tag : Str -> Str

let identity (s: Str) = s

let apply (f: Str -> Str) (s: Str) =
    handle f(s) with
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
            |> apply(identity)
        in loop(n - 1)(total + Ashes.Text.byteLength(kept))

0
|> loop(200000)
|> (given (_) -> Ashes.IO.print("ok"))
