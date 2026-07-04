// expect: 200000|6813315380
// CO-15: Ashes.HashMap.set is now in-place-reuse specialized like Ashes.Map.set. HashMap.set has
// the eta-applied nested-recursive shape (`let target = hashKey(k) in let rec go tree = ... in
// go(map)`) rather than Map.set's bare `... in go`, and its per-node composite-key descent inlines
// strCompare's own `let rec go i = ... in go(0)` helper — a closure that is stored to a slot and
// immediately called, which used to make IsFullyReusing reject the whole spec (so a HashMap-keyed
// fold leaked ~10 GB at 1M inserts vs Map's constant memory). This inserts 200k distinct growing
// string keys k<i> -> i*3, updates every 7th key by +1000000, then reads back a spread of keys and
// sums their values. A reuse use-after-free would corrupt a stored value or key and change the
// checksum; the expected value is computed independently.
import Ashes.HashMap
import Ashes.Text
import Ashes.IO
let recursive build i n m = 
    if i >= n
    then m
    else build(i + 1)(n)(Ashes.HashMap.set("k" + Ashes.Text.fromInt(i))(i * 3)(m))

let recursive bump i n m = 
    if i >= n
    then m
    else 
        let m2 = 
            if i - i / 7 * 7 == 0
            then Ashes.HashMap.set("k" + Ashes.Text.fromInt(i))(i * 3 + 1000000)(m)
            else m
        in bump(i + 1)(n)(m2)

let recursive checksum i n m acc = 
    if i >= n
    then acc
    else 
        let v = 
            match Ashes.HashMap.get("k" + Ashes.Text.fromInt(i))(m) with
                | Some(x) -> x
                | None -> -1
        in checksum(i + 13)(n)(m)(acc + v)

let n = 200000

let built = build(0)(n)(Ashes.HashMap.empty)

let bumped = bump(0)(n)(built)
in Ashes.IO.print(Ashes.Text.fromInt(Ashes.HashMap.size(bumped)) + "|" + Ashes.Text.fromInt(checksum(0)(n)(bumped)(0)))
