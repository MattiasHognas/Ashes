// expect: 42
import Library
let box : Box = Library.make(2)

Ashes.IO.print(match Library.Yes with
    | Yes -> 42
    | _ -> 0)
