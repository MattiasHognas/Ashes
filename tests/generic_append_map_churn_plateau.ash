// expect: 400000
// A loop that builds two generic map results (records with a string field), appends them, and
// discards the appended list every iteration. The generic callee's result is deep-copied out of
// its call window, so the consumed inputs share nothing with it and are released with their
// elements; keeping only their spines leaked every record and string once per iteration
// (32.8 MB at 200000 iterations against 8.2 MB at 20000, 20.5 MB with the deep release). The
// remaining growth is the owned copy of `toEntry`'s always-returned parameter orphaned in its
// arena record, tracked under OPT-41 in the self-hosting plan.
import Ashes.Collection.List as list
type Item =
    | name: Str
    | flag: Bool

let toEntry (n: Str) = Item(name = n, flag = true)

let recursive loop i acc =
    if i == 0
    then acc
    else
        let entries = list.append(list.map(toEntry)([Ashes.Text.fromInt(i)]))(list.map(toEntry)(["Testing"]))
        in loop(i - 1)(acc + list.length(entries))

Ashes.IO.print(Ashes.Text.fromInt(loop(200000)(0)))
