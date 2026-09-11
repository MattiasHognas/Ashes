// Ashes.Collection.List.foldLeft(f)(init)(Ashes.Collection.List.map(g)(xs)) is fused into a single
// pass applying g then f per element, never materializing the mapped list, whenever both callbacks
// are proven total (a closed grammar of literals/variables/arithmetic-comparison-boolean operators
// and `if`, never division/remainder/a call) over a type whose Add/Multiply/etc behavior is fixed by
// the language, not overridable by a user trait implementation. Covers: a named top-level callback,
// a literal lambda callback, an empty list, a single-element list, and a Str (pointer-bearing,
// reference-counted) element type — the fusion must retain/release exactly as the unfused chain
// would, since a fused element is applied through g then f as an ordinary value, not relocated.
// expect: 55|12|0|9|a!b!c!
import Ashes.Collection.List as list
let square x = x * x

let add a b = a + b

let namedResult =
    [1, 2, 3, 4, 5]
    |> list.map(square)
    |> list.foldLeft(add)(0)

let literalResult =
    [1, 2, 3]
    |> list.map(given (x) -> x * 2)
    |> list.foldLeft(given (a) ->
        given (b) -> a + b)(0)

let emptyResult =
    []
    |> list.map(square)
    |> list.foldLeft(add)(0)

let singletonResult =
    [3]
    |> list.map(square)
    |> list.foldLeft(add)(0)

let exclaim s = s + "!"

let concat a b = a + b

let strResult =
    ["a", "b", "c"]
    |> list.map(exclaim)
    |> list.foldLeft(concat)("")

Ashes.IO.print(
    Ashes.Text.fromInt(namedResult) + "|" + Ashes.Text.fromInt(literalResult) + "|" + Ashes.Text.fromInt(emptyResult) + "|" + Ashes.Text.fromInt(singletonResult) + "|" + strResult
)
