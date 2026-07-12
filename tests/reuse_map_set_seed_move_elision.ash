// expect: 5 40
// CO-2c: admit a Map.set RESULT as a move seed. `Ashes.Map.set(k)(v)(m)` returns its accumulator `m`,
// rebuilt/rebalanced in place on the reuse path, so seeding a reuse fold from `Ashes.Map.set(...)(m)` —
// where `m` is itself a move — yields a uniquely-owned map and the fold's entry deep-copy is ELIDED.
// The result-reachability summary computes Map.set's reach as {map, newKey, newValue} (not poisoned):
// the accumulator plus the inserted key and value, each of which must independently be a move at the
// seed site (here integer literals, which are moves). Before CO-2c, Map.set's reach was poisoned (it
// returns a nested recursive `go`, and its `balance`/rotate helpers falsely reported internal sharing
// when destructuring and rebuilding a node), so the seed was never admitted and the copy stayed.
//
// `outer` threads a growing map, re-seeding the inner reuse fold `inner` from `Ashes.Map.set(...)(m)`
// on every re-entry — exactly the O(re-entries * map-size) redundant-copy shape CO-2c removes. This
// asserts CORRECTNESS at small sizes (the peak-RSS win is measured out-of-band): after 3 rounds the
// map has 5 keys (0..4) and key 4 maps to 40.
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

let recursive inner i lim m =
    if i > lim
    then m
    else inner(i + 1)(lim)(Ashes.Map.set(cmp)(i)(i * 10)(m))

let recursive outer b nb m =
    if b >= nb
    then m
    else outer(b + 1)(nb)(inner(0)(4)(Ashes.Map.set(cmp)(0)(0)(m)))

let seeded = Ashes.Map.set(cmp)(0)(0)(Ashes.Map.empty)

let final = outer(0)(3)(seeded)
in
    match Ashes.Map.get(cmp)(4)(final) with
        | None -> Ashes.IO.print("fail")
        | Some(v) -> Ashes.IO.print(Ashes.Text.fromInt(Ashes.Map.size(final)) + " " + Ashes.Text.fromInt(v))
