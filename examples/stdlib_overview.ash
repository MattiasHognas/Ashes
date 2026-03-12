
import Ashes.IO
import Ashes.List
import Ashes.Maybe
import Ashes.Result
let nums = [1, 2, 3, 4, 5]
in 
    let doubled = 
        map(fun (x) -> x * 2)(nums)
    in 
        let maybeCount = Some(length(doubled))
        in 
            let resultCount = 
                map(fun (count) -> count + 1)(Ok(getOrElse(0)(maybeCount)))
            in 
                match resultCount with
                    | Ok(value) -> print(value)
                    | Error(_) -> print(0)
