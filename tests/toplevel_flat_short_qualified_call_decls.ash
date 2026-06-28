// expect: 14
import Ashes.IO
import Ashes.List
import Ashes.Maybe
import Ashes.Result
let nums = [1, 2, 3, 4, 5]

let doubled = 
    List.map(fun (x) -> x * 2)(nums)

let large = 
    List.filter(fun (x) -> x >= 6)(doubled)

let count = List.length(large)

let maybeTop = List.head(List.reverse(large))

let adjusted = 
    Maybe.map(fun (x) -> x + count)(maybeTop)

let safeValue = Maybe.getOrElse(0)(adjusted)

let resultValue = 
    Result.map(fun (x) -> x + 1)(Ok(safeValue))

let final = Result.getOrElse(0)(resultValue)

print(final)
