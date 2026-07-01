// expect: 100 150
// Nested re-entry of an in-place-reuse fold (roadmap CO-2). `setFold` is a flat loop that threads a
// Map accumulator (last arg to `Ashes.Map.set`, so `set` specializes to `set$reuse` and the fold's
// accumulator is deep-copied once at the fold's entry to make it uniquely owned). `outer` then calls
// `setFold` once per batch, threading the *same* growing map through each call. This guards
// CORRECTNESS of that shape: nested reuse must never corrupt the accumulator — after 30 batches the
// map still has 100 distinct keys and key 50 still maps to 150 (= 50 * 3), identical to a single flat
// fold. The known remaining issue is MEMORY, not correctness: the fold's entry deep-copy re-executes
// on every outer re-entry and re-copies the whole map (~map-size per batch, never reclaimed), an
// O(batches * map-size) leak that a single flat loop avoids. That leak is not asserted here (the test
// harness cannot assert peak RSS); eliminating it needs interprocedural move/linearity analysis to
// prove the accumulator is moved (not aliased after the call) at every call site — the ownership
// milestone. Sizes are kept small so this stays a fast, green correctness regression.
import Ashes.Map
import Ashes.IO
import Ashes.Text
let cmp a b = 
    if a == b
    then 0
    else 
        if a <= b
        then -1
        else 1

let rec setFold i lim m = 
    if i > lim
    then m
    else setFold(i + 1)(lim)(Ashes.Map.set(cmp)(i)(i * 3)(m))

let rec outer batch nbatch m = 
    if batch >= nbatch
    then m
    else outer(batch + 1)(nbatch)(setFold(0)(99)(m))

let final = outer(0)(30)(Ashes.Map.empty)
in 
    match Ashes.Map.get(cmp)(50)(final) with
        | None -> Ashes.IO.print("fail")
        | Some(v) -> Ashes.IO.print(Ashes.Text.fromInt(Ashes.Map.size(final)) + " " + Ashes.Text.fromInt(v))
