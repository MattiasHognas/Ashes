// expect: 3|3|3|3|4|hi|2|true
// A fully-qualified reference to a shipped standard library module needs no import: the
// module is stitched in because the reference names it. Every position a qualified
// reference can occupy is covered, since each used to fail with "Unknown module".
let bare = Ashes.Collection.List.length

let plain = Ashes.Collection.List.length([1, 2, 3])

let sugar xs = Ashes.Collection.List.length(xs)

let lambda =
    given (xs) -> Ashes.Collection.List.length(xs)

let entries = Ashes.Collection.Map.fromList([("a", 1), ("b", 2)])

let counts = [bare([1, 2, 3]), plain, sugar([1, 2, 3]), lambda([1, 2, 3]), Ashes.Collection.List.length([1, 2, 3, 4])]

let rendered =
    counts
    |> Ashes.Collection.List.map(given (n) -> Ashes.Text.fromInt(n))
    |> Ashes.Text.join("|")

let present =
    if Ashes.Core.Maybe.isSome(Some(1))
    then "true"
    else "false"

Ashes.IO.print(rendered + "|" + Ashes.Text.trim("  hi  ") + "|" + Ashes.Text.fromInt(Ashes.Collection.Map.size(entries)) + "|" + present)
