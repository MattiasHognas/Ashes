// A parenthesized `let` after an operator whose value is a nested `let ... in` laid out left of
// the outer `let`, and a second one whose lambda value starts on such a line: each value runs to
// its own `in`, not to the first line at the declaration's column.
// expect: qlq]
let r =
    "q" + (let g : Str =
        let s =
            let lf =
                given (a: Int) -> "l"
            in lf(-3)
        in s
    in g) + "q"

let t =
    (let recursive close : List(Int) -> Str =
        given (items: List(Int)) ->
            match items with
                | [] -> "]"
                | _ :: tail -> close(tail)
    in close([1]))

Ashes.IO.print(r + t)
