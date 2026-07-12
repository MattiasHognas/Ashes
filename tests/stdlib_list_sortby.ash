// Ashes.List.sortBy: comparator-based merge sort (O(n log n), stable via alternating split). The
// benchmarks (k-nucleotide) had to hand-write this; it now ships in the stdlib. `before(a)(b)` is true
// when a should not come after b.
// expect: 0,1,2,3,4,5,6,7,8,9, | 9,8,7,5,3,1, |  | 42,
import Ashes.List as list
import Ashes.IO as io
import Ashes.Text as text
let recursive show xs =
    match xs with
        | [] -> ""
        | h :: t -> text.fromInt(h) + "," + show(t)

let asc a b = a <= b

let desc a b = a >= b

io.print(show(list.sortBy(asc)([5, 3, 8, 1, 9, 2, 7, 4, 6, 0])) + " | " + show(list.sortBy(desc)([5, 3, 8, 1, 9, 7])) + " | " + show(list.sortBy(asc)([])) + " | " + show(list.sortBy(asc)([42])))
