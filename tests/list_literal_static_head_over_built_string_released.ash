// expect: 40
// A function returns a list literal whose middle string is built at run time and whose other
// strings are static. The built string's cell is reference-counted while a static string's cell
// would stay in the arena, where it would be the only holder of the cell after it; the static
// head is copied onto the reference-counted heap instead, so the whole list is released by its
// consumer, and the list still reads back as it was written.
let recursive totalLength (items: List(Str)) (sum: Int) =
    match items with
        | [] -> sum
        | item :: rest -> totalLength(rest)(sum + Ashes.Text.byteLength(item))

let labels (n: Int) = ["c", "d" + Ashes.Text.fromInt(n), "e"]

let recursive run (n: Int) =
    if n < 0
    then run(n + 1)
    else
        totalLength(labels(n))(0)

let recursive rounds (n: Int) (sum: Int) =
    if n == 0
    then sum
    else rounds(n - 1)(sum + run(n) + 6)

0
|> rounds(4)
|> Ashes.IO.print
