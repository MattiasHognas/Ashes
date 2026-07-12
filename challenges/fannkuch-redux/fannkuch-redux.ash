// fannkuch-redux -- Benchmarks Game challenge, done fully purely (no mutation).
//
// Enumerate every permutation of [1..N] in the reference's successive (factorial-number-system
// rotation) order; for each, repeatedly reverse the first p[0] elements until p[0]==1, counting
// the reversals. Print the running checksum and the maximum flip count (Pfannkuchen(N)).
//
// The permutation is a plain List(Int): reading p[0] is O(1) head, and `flip` reverses the first k
// in a single O(k) pass (NOT stdlib take+reverse+append, which is quadratic). Every flip and
// rotation allocates a fresh list, so the combinatorial loop is a pure allocation-churn probe.
// N! grows factorially, so the Benchmarks Game standard N=12 is out of reach for a pure immutable
// enumeration -- benchmark at small N.
//
// STATUS: runs correctly. The three compiler bugs this benchmark surfaced (a two-threaded-list
// early-return miscompile, a spurious ASH014, and a TCO back-edge use-after-reset segfault) are all
// fixed. The permutation and its factorial counter are modeled as one `State(perm,
// count)` pair, threaded through the enumeration loop; that is a design choice, not a workaround now.
// N! grows factorially, so resident memory grows with the enumeration (the growing pointer-bearing
// accumulator is not reclaimed within the loop yet -- the memory-model milestone), and the Benchmarks
// Game standard N=12 is out of reach; benchmark at small N.
//
// Usage: ./fannkuch-redux 7   (defaults to 7)
import Ashes.IO as io
import Ashes.Text as text
let recursive iota i n =
    if i > n
    then []
    else i :: iota(i + 1)(n)

let recursive zeros n =
    if n == 0
    then []
    else 0 :: zeros(n - 1)

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

let recursive insertAt i v xs =
    if i == 0
    then v :: xs
    else
        match xs with
            | [] -> v :: []
            | h :: t -> h :: insertAt(i - 1)(v)(t)

let rotateFirst r xs =
    match xs with
        | [] -> []
        | h :: t -> insertAt(r)(h)(t)

let recursive appendTail acc xs =
    match acc with
        | [] -> xs
        | h :: t -> h :: appendTail(t)(xs)

let recursive flipInto k xs acc =
    if k == 0
    then appendTail(acc)(xs)
    else
        match xs with
            | [] -> appendTail(acc)(xs)
            | h :: t -> flipInto(k - 1)(t)(h :: acc)

let flip k xs = flipInto(k)(xs)([])

let recursive countFlips perm flips =
    match perm with
        | [] -> flips
        | h :: _ ->
            if h == 1
            then flips
            else countFlips(flip(h)(perm))(flips + 1)

type State =
    | S(List(Int), List(Int))

type Step =
    | Done
    | Continue(State, Int)

let recursive nextPerm r n st =
    if r == n
    then Done
    else
        match st with
            | S(perm, count) ->
                let perm2 = rotateFirst(r)(perm)
                in
                    let cr = getAt(r)(count) - 1
                    in
                        let count2 = setAt(r)(cr)(count)
                        in
                            if cr > 0
                            then Continue(S(perm2)(count2))(r)
                            else nextPerm(r + 1)(n)(S(perm2)(count2))

let recursive resetCounts r count =
    if r == 1
    then count
    else resetCounts(r - 1)(setAt(r - 1)(r)(count))

let recursive loop n st r sign maxFlips checksum =
    match st with
        | S(perm, count) ->
            let count1 = resetCounts(r)(count)
            in
                let flips = countFlips(perm)(0)
                in
                    let maxFlips2 =
                        if flips > maxFlips
                        then flips
                        else maxFlips
                    in
                        let checksum2 = checksum + sign * flips
                        in
                            match nextPerm(1)(n)(S(perm)(count1)) with
                                | Done -> text.fromInt(checksum2) + "\nPfannkuchen(" + text.fromInt(n) + ") = " + text.fromInt(maxFlips2) + "\n"
                                | Continue(st2, r2) -> loop(n)(st2)(r2)(-sign)(maxFlips2)(checksum2)

let fannkuch n = loop(n)(S(iota(1)(n))(zeros(n)))(n)(1)(0)(0)

match io.args with
    | arg :: _ ->
        match text.parseInt(arg) with
            | Ok(n) -> io.print(fannkuch(n))
            | Error(_) -> io.print(fannkuch(7))
    | [] -> io.print(fannkuch(7))
