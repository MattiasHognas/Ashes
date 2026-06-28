// Regression: a curried 2-argument `let rec` whose BOTH recursive-call arguments
// are freshly heap-allocated string tails used to deadlock (rt_sigsuspend) at
// recursion depth >= 12, at every -O level. The defect was in the TCO back-edge
// copy-out: compacting both fresh heap args down to the arena watermark with
// overlapping forward copies clobbered the second arg's still-unread source.
//
// This exercises the SHIPPED stdlib victims directly at depth >= 20:
//   - Ashes.String.startsWith  (textTail / prefixTail, both fresh string tails)
//   - Ashes.Json.consumeExact  (wantRest / gotRest, both fresh string tails)
// expect: pass
import Ashes.String
import Ashes.Json
import Ashes.IO
let long = "abcdefghijklmnopqrstuvwxyz0123456789"
in 
    let startsWithOk = Ashes.String.startsWith(long)(long)
    in 
        let consumeExactOk = 
            match Ashes.Json.consumeExact(long)(long) with
                | Ok(_) -> true
                | Error(_) -> false
        in 
            Ashes.IO.print(if startsWithOk
            then 
                if consumeExactOk
                then "pass"
                else "fail-consumeExact"
            else "fail-startsWith")
