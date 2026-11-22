// A helper compares its list parameter with the empty list; its only caller is a generic wrapper,
// and the program uses that wrapper at one element type.
// expect: a:some b:some
let hasAny selected =
    if selected != []
    then "some"
    else "none"

let describe name selected = name + ":" + hasAny(selected)

Ashes.IO.print(describe("a")(["x"]) + " " + describe("b")(["y", "z"]))
