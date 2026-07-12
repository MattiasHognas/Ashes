// Regression: the TCO back-edge arena reset used a single-cell shallow copy-out for list
// accumulators, which preserves only the top cons cell. That is sound only when the argument is
// exactly `head :: <loop param>` (one fresh cell, tail below the watermark). A list rebuilt each
// iteration (setAt), or `a :: b :: acc` (two fresh cells), left interior cells dangling: the reset
// reclaimed still-referenced memory, so the loop took the wrong branch or segfaulted. Now any list
// accumulator that is not a single-fresh-cons disqualifies the reset (no reclamation) instead.
//
// Cases below: (1) a setAt-rebuilt single list, (2) two independently threaded lists with an early
// ADT return, (3) a two-fresh-cell cons -- each previously corrupted; (4) the plain cons-growing
// accumulator must still reset (it stays the fast path, verified indirectly by producing the right
// sum without unbounded growth).
// expect: 11,12,13,14, | HIT 2 | 2,2,1,1,0,0, | 499500
import Ashes.IO as io
import Ashes.Text as text
type R =
    | Base
    | Hit(Int)

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

let recursive bump r n count =
    if r == n
    then show(count)
    else bump(r + 1)(n)(setAt(r)(getAt(r)(count) + 10)(count))

let recursive scan r n perm count =
    if r == n
    then Base
    else
        let cr = getAt(r)(count) - 1
        in
            if cr > 0
            then Hit(r)
            else scan(r + 1)(n)(r :: perm)(setAt(r)(cr)(count))

let recursive twin r n acc =
    if r == n
    then show(acc)
    else twin(r + 1)(n)(r :: r :: acc)

let recursive range i n acc =
    if i == n
    then acc
    else range(i + 1)(n)(i :: acc)

let recursive sum xs acc =
    match xs with
        | [] -> acc
        | h :: t -> sum(t)(acc + h)

let scanResult =
    match scan(1)(3)([2, 1, 3])([0, 1, 3]) with
        | Base -> "BASE"
        | Hit(r) -> "HIT " + text.fromInt(r)

io.print(bump(0)(4)([1, 2, 3, 4]) + " | " + scanResult + " | " + twin(0)(3)([]) + " | " + text.fromInt(sum(range(0)(1000)([]))(0)) + "\n")
