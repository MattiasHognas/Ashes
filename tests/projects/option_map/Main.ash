// expect: 2
import Option
let inc = 
    fun (x) -> x + 1
in 
    match Option.map(inc)(Some(1)) with
        | Some(x) -> Ashes.IO.print(x)
        | None -> Ashes.IO.print(0)
