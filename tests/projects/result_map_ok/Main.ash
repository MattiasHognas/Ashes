// expect: 2
import Result
let inc = 
    fun (x) -> x + 1
in 
    match Result.map(inc)(Ok(1)) with
        | Ok(x) -> Ashes.IO.print(x)
        | Error(_) -> Ashes.IO.print(0)
