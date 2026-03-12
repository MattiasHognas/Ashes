import Ashes.IO
import Ashes.List
import Ashes.Maybe
import Ashes.Result
let nums = [1, 2, 3, 4, 5]
in 
    let doubled = 
        List.map(fun (x) -> x * 2)(nums)
    in 
        let large = 
            List.filter(fun (x) -> x >= 6)(doubled)
        in 
            let count = List.length(large)
            in 
                let maybeTop = List.head(List.reverse(large))
                in 
                    let maybeAdjusted = 
                        Maybe.map(fun (x) -> x + count)(maybeTop)
                    in 
                        let safeValue = 
                            if Maybe.isSome(maybeAdjusted)
                            then Maybe.getOrElse(0)(maybeAdjusted)
                            else 0
                        in 
                            let resultValue = 
                                Result.map(fun (x) -> x + 1)(Ok(safeValue))
                            in 
                                if Result.isOk(resultValue)
                                then 
                                    match resultValue with
                                        | Ok(value) -> print(value)
                                        | Error(_) -> print(0)
                                else print(0)
