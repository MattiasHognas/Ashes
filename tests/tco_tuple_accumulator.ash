// Regression: a tuple threaded as a TCO accumulator (e.g. fasta's (seed, output)) is carried across
// the back-edge arena reset by a recursive deep copy of the tuple (each element deep-copied), a
// self-contained clone that resets to the fixed loop-entry watermark. Before this, a tuple accumulator
// was not in the copy-out path at all, so its growing String field accumulated O(N^2) resident memory
// (fasta N=20000 -> 3.97 GB). This pins CORRECTNESS: a (Int, String) tuple with a growing String and a
// threaded LCG seed is run for many iterations and the exact result is checked.
// expect: len=30 seed=61356 str=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
import Ashes.IO as io
import Ashes.Text as text
let recursive go i n st =
    match st with
        | (seed, out) ->
            if i == n
            then "len=" + text.fromInt(text.byteLength(out)) + " seed=" + text.fromInt(seed) + " str=" + out
            else go(i + 1)(n)((seed * 3877 + 29573 - (seed * 3877 + 29573) / 139968 * 139968, out + "a"))

io.print(go(0)(30)((42, "")))
