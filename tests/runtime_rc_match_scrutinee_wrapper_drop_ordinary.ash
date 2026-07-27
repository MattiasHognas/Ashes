// Regression: the same fresh-wrapper-with-extracted-field shape as
// runtime_rc_tco_match_scrutinee_wrapper_drop.ash, but reached through an ordinary (non-tail-call,
// non-loop) match, an extracted field that is never forwarded anywhere, and an extracted field used
// twice plus one destructured a second time by a nested match. Confirms the wrapper-drop fix does not
// disturb any of these more common shapes: no crash, no use-after-drop, correct output.
// expect: 3 7 5
import Ashes.IO as io
import Ashes.Text as text
type Pair =
    | S(List(Int), Int)

type Step =
    | Done
    | Continue(Pair, Int)

let recursive nextStep n st =
    if n <= 0
    then Done
    else
        match st with
            | S(xs, c) -> Continue(S(xs)(c + 1))(n)

let recursive pairLength xs =
    match xs with
        | [] -> 0
        | _ :: rest -> 1 + pairLength(rest)

let readOnce st =
    match nextStep(3)(st) with
        | Done -> 0
        | Continue(st2, r2) -> r2

let usedTwice st =
    match nextStep(3)(st) with
        | Done -> 0
        | Continue(st2, r2) ->
            match st2 with
                | S(xs, c) -> pairLength(xs) + pairLength(xs) + c

let nested st =
    match nextStep(3)(st) with
        | Done -> 0
        | Continue(st2, r2) ->
            match st2 with
                | S(xs, c) -> c + r2

io.print(text.fromInt(readOnce(S([9])(5))) + " " + text.fromInt(usedTwice(S([1, 2, 3])(0))) + " " + text.fromInt(nested(S([4, 5])(1))))
