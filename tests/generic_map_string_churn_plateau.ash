// expect: 400000
// The string sibling of generic_append_map_churn_plateau: two generic map results carrying fresh
// strings are appended and discarded every iteration. The appended result takes the conditional
// list copy-out; on its arena branch the heads are copied too, so the consumed first argument is
// released with its strings there and spine-only on the owned branch. Releasing spine-only on
// both branches leaked one string per element (12.3 MB at 200000 iterations against 8.2 MB at
// 20000; flat now).
import Ashes.Collection.List as list
let bang (n: Str) = n + "!"

let recursive loop i acc =
    if i == 0
    then acc
    else
        let entries = list.append(list.map(bang)([Ashes.Text.fromInt(i)]))(list.map(bang)(["Testing"]))
        in loop(i - 1)(acc + list.length(entries))

Ashes.IO.print(Ashes.Text.fromInt(loop(200000)(0)))
