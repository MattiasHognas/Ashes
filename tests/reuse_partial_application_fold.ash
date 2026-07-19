// expect: 15
// A list-building fold bound via a partial application (let g = build(5)) and then completed
// (g(Nil)) is now analyzable: the accumulator is proven uniquely owned through the resolved call
// site, exactly as if build were called directly, so its entry deep-copy can be elided. Asserts
// correctness (a wrong elision would corrupt the accumulator); the memory win is measured out-of-band.
import Ashes.IO
import Ashes.Text
type L =
    | Nil
    | Cons(Int, L)

let recursive build n acc =
    if n <= 0
    then acc
    else build(n - 1)(Cons(n)(acc))

let recursive sumL xs =
    match xs with
        | Nil -> 0
        | Cons(h, t) -> h + sumL(t)

let g = build(5)

let lst = g(Nil)
in Ashes.IO.print(Ashes.Text.fromInt(sumL(lst)))
