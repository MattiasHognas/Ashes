// expect: 42
import Ashes.Trait
let render value = Show.show(value)

let recursive addAll values total =
    match values with
        | [] -> total
        | value :: rest -> addAll(rest)(total + value)

Ashes.IO.print(render(addAll([20, 22])(0)))
