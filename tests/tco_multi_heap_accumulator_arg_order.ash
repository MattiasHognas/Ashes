// expect: 11 15
// CO-9 regression: a TCO loop threading two heap accumulators of DIFFERENT kinds must store each
// back-edge argument into its own parameter slot regardless of parameter order.
//
// The loop's param slots were built in capture-DISCOVERY order (the order free variables first appear
// in the body), but the back-edge stored argument i into slot i assuming DECLARATION order. When the
// two orders differ — here `loopA s xs n` whose body mentions `s` before `xs` while a growing string
// and a growing list are threaded — the string and list pointers were written into each other's slots
// (a swap), so the next iteration read a list through the string slot and vice versa, corrupting both
// and crashing. `loopB` puts the string AFTER an in-place-reused ADT accumulator to exercise the same
// swap with a reuse-managed accumulator. Both shapes SIGSEGV'd pre-fix; the fix builds the param slots
// in parameter order so ParamSlots[i] is always the i-th parameter's (and i-th argument's) slot.
import Ashes.IO
import Ashes.Text
type Acc =
    | Acc(Int, Int)

let recursive llen xs acc = 
    match xs with
        | [] -> acc
        | h :: t -> llen(t)(acc + 1)

let recursive slen text acc = 
    match Ashes.Text.uncons(text) with
        | None -> acc
        | Some((_h, tl)) -> slen(tl)(acc + 1)

let recursive loopA s xs n = 
    if n <= 0
    then llen(xs)(0) + slen(s)(0)
    else loopA(s + "z")(n :: xs)(n - 1)

let recursive loopB s p n = 
    match p with
        | Acc(a, b) -> 
            if n <= 0
            then a + b + slen(s)(0)
            else loopB(s + "q")(Acc(a + 1)(b + 1))(n - 1)

let ra = loopA("")(0 :: [])(5)

let rb = loopB("")(Acc(0)(0))(5)

Ashes.IO.print(Ashes.Text.fromInt(ra) + " " + Ashes.Text.fromInt(rb))
