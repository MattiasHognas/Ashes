// expect: 3000000
// Perceus unification Phase 5 adversarial guard: one if-arm a bare-Var passthrough of an existing,
// not provably fresh list binding, the other arm a genuinely fresh list literal. Direct IR inspection
// showed an existence-check (OR) classifier RC-allocates the fresh arm's cons cell while the
// join-level bookkeeping declines to own it -- a real bookkeeping disagreement, fixed regardless of
// its runtime impact. Looped at real scale here to check for actual RSS growth: comparing pre-fix and
// post-fix compiled binaries at up to 300,000 iterations (half taking the fresh arm) found no
// measurable difference -- the orphaned cell appears to be reclaimed by the same scope-based bulk
// arena reset that reclaims ordinary arena garbage rather than leaking outright. This test is kept as
// a correctness/no-crash regression guard (and a canary if that reclaim path ever changes), not as
// proof of a fixed leak.
import Ashes.Text as text
import Ashes.IO as io
let existingList =
    (let p = "p"
    in
        let q = "q"
        in [p, q])

let build flag =
    (let discard = 0
    in
        if flag
        then existingList
        else [text.fromInt(1)])

let recursive sumLengths values acc =
    match values with
        | [] -> acc
        | head :: rest -> sumLengths(rest)(acc + text.byteLength(head))

let recursive repeat n acc =
    if n == 0
    then acc
    else repeat(n - 1)(acc + sumLengths(build(n % 2 == 0))(0))

io.print(text.fromInt(repeat(2000000)(0)))
