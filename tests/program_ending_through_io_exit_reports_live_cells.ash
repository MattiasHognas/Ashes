// exit: 3
// expect: done 6
// A program ending through `Ashes.IO.exit`, as the compiler's own CLI does, runs the same epilogue
// as one returning from its entry: built with the leak check, it reports its live reference-counted
// cells before the process exits with the chosen code.
let words = ["a" + Ashes.Text.fromInt(1), "b" + Ashes.Text.fromInt(2), "c"]

let recursive total (items: List(Str)) (acc: Int) =
    match items with
        | [] -> acc
        | item :: rest -> total(rest)(acc + Ashes.Text.byteLength(item))

let _ =
    Ashes.IO.print("done " + Ashes.Text.fromInt(total(words)(1)))

Ashes.IO.exit(3)
