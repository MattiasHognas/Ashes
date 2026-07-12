// Regression: a non-recursive pointer-bearing ADT threaded as a TCO accumulator (fannkuch's
// State(perm, count) shape) is carried across the back-edge arena reset by a recursive DEEP copy --
// a self-contained clone whose list fields are fully copied, breaking any tail-sharing with the
// previous accumulator. That both lets the reset fire (fannkuch N=10 dropped from 4.6 GB to a
// constant 0.25 MB) and, since the clone is self-contained, resets to the fixed loop-entry watermark
// so the accumulator stays O(current size). Self-recursive ADTs (trees) are deliberately excluded --
// they are owned by the in-place reuse specialization. This pins CORRECTNESS: the State's two lists
// are rebuilt (setAt) and consed each step, with an early ADT return, and the exact result is checked.
// expect: HIT r=2 perm=2,1,0, count=10,11,13,
import Ashes.IO as io
import Ashes.Text as text
type State =
    | S(List(Int), List(Int))

type Step =
    | Done(List(Int), List(Int))
    | Hit(Int, List(Int), List(Int))

let recursive getAt i xs =
    match xs with
        | [] -> 0
        | h :: t ->
            if i == 0
            then h
            else getAt(i - 1)(t)

let recursive setAt i v xs =
    match xs with
        | [] -> []
        | h :: t ->
            if i == 0
            then v :: t
            else h :: setAt(i - 1)(v)(t)

let recursive show xs =
    match xs with
        | [] -> ""
        | h :: t -> text.fromInt(h) + "," + show(t)

let recursive walk r n st =
    match st with
        | S(perm, count) ->
            if r == n
            then Done(perm)(count)
            else
                let perm2 = r :: perm
                in
                    let cr = getAt(r)(count) + 10
                    in
                        let count2 = setAt(r)(cr)(count)
                        in
                            if r == 2
                            then Hit(r)(perm2)(count2)
                            else walk(r + 1)(n)(S(perm2)(count2))

let result =
    match walk(0)(5)(S([])([0, 1, 3])) with
        | Done(p, c) -> "DONE perm=" + show(p) + " count=" + show(c)
        | Hit(r, p, c) -> "HIT r=" + text.fromInt(r) + " perm=" + show(p) + " count=" + show(c)

io.print(result)
