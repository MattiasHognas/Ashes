// expect: 20668894
// A function whose result is its own entry-normalized parameter returns a reference-counted
// value whichever way the argument arrived, so its closure advertises a runtime-managed result
// and honors a generic caller's arena-result request. Applied through a parameter function,
// whose caller cannot own a reference-counted result, the returned string used to be copied by
// the caller while the original was never released: 512 bytes leaked per iteration.

let identity (s: Str) = s

let apply f (s: Str) = f(s)

let recursive fill (n: Int) (acc: Str) =
    if n == 0
    then acc
    else fill(n - 1)(acc + "abcdefgh")

let recursive loop (n: Int) (total: Int) =
    if n == 0
    then total
    else
        let kept = apply(identity)(fill(64)(Ashes.Text.fromInt(n)))
        in loop(n - 1)(total + Ashes.Text.byteLength(kept))

Ashes.IO.print(Ashes.Text.fromInt(loop(40000)(0)))
