// expect: 2 1:2 1000:2000 10000:20000
// A generic recursive function with an Eq requirement, exported from another module beside a
// private helper: the closure its first stage returns is copied out of the call's arena window,
// and with it the function's own closure, captured to recurse through and built in that window.
// At 1, 1,000 and 10,000 calls.
import Paths
let root = "/r"

let paths = [root + "a", root + "b", root + "a"]

let count (seen: List(Str)) =
    seen
    |> uniquePaths(paths)
    |> Ashes.Collection.List.length

let recursive rounds (n: Int) (acc: Int) =
    if n == 0
    then acc
    else rounds(n - 1)(acc + count([]))

let total n =
    Ashes.Text.fromInt(n) + ":" + Ashes.Text.fromInt(rounds(n)(0))

let first =
    []
    |> count
    |> Ashes.Text.fromInt

[first, total(1), total(1000), total(10000)]
|> Ashes.Text.join(" ")
|> Ashes.IO.print
