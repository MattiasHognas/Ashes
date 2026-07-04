// expect: 100 15 902
// CO-2 move/linearity copy elision — soundness discriminator in one program.
//
// `setFold` threads a Map accumulator into `Ashes.Map.set` (so `set` specializes to `set$reuse`).
// Its entry deep-copy is ELIDED because the whole-program move analysis proves the accumulator is
// uniquely owned at every external call site: `outer` passes its own move-safe accumulator `m`
// (used once per path, seeded from the sole-nullary `Ashes.Map.empty`), and `let base = ...` passes
// `Ashes.Map.empty` directly. Elision makes the nested re-entry (`outer` calling `setFold` per batch)
// constant-memory instead of O(batches * map-size); `nestedResult` still has 100 keys.
//
// `bumpKey5` threads the SAME shape, but its only call site passes `base`, which is ALSO retained
// (as `keep` and read afterward). The analysis therefore DECLINES to elide bumpKey5's copy — the
// arg is not a move — so the in-place update of key 5 rewrites bumpKey5's own copy, leaving `base`
// (aliased by `keep`) intact: `keep` still reads 5*3 = 15, while `updated` reads 900+2 = 902. A
// wrongly-enabled elision here would corrupt `keep` to 902.
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

let getv m k = 
    match Ashes.Map.get(cmp)(k)(m) with
        | None -> -1
        | Some(v) -> v

let recursive setFold i lim m = 
    if i > lim
    then m
    else setFold(i + 1)(lim)(Ashes.Map.set(cmp)(i)(i * 3)(m))

let recursive outer b nb m = 
    if b >= nb
    then m
    else outer(b + 1)(nb)(setFold(0)(99)(m))

let recursive bumpKey5 b nb m = 
    if b >= nb
    then m
    else bumpKey5(b + 1)(nb)(Ashes.Map.set(cmp)(5)(900 + b)(m))

let nestedResult = outer(0)(5)(Ashes.Map.empty)

let base = setFold(0)(20)(Ashes.Map.empty)

let keep = base

let updated = bumpKey5(0)(3)(base)
in Ashes.IO.print(Ashes.Text.fromInt(Ashes.Map.size(nestedResult)) + " " + Ashes.Text.fromInt(getv(keep)(5)) + " " + Ashes.Text.fromInt(getv(updated)(5)))
