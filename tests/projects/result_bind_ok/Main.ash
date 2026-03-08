// expect: 6
import Result
let addOneIfSmall = 
    fun (x) -> 
        if x <= 5
        then Ok(x + 1)
        else Error(0)
in 
    match Result.bind(addOneIfSmall)(Ok(5)) with
        | Ok(x) -> Ashes.IO.print(x)
        | Error(_) -> Ashes.IO.print(0)
