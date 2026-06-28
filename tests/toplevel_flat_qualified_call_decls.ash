// expect: 14
import Ashes.IO
import Ashes.List
import Ashes.Maybe
import Ashes.Result
let nums = [1, 2, 3, 4, 5]

let doubled = 
    Ashes.List.map(fun (x) -> x * 2)(nums)

let large = 
    Ashes.List.filter(fun (x) -> x >= 6)(doubled)

let count = Ashes.List.length(large)

let maybeTop = Ashes.List.head(Ashes.List.reverse(large))

let adjusted = 
    Ashes.Maybe.map(fun (x) -> x + count)(maybeTop)

let safeValue = Ashes.Maybe.getOrElse(0)(adjusted)

let resultValue = 
    Ashes.Result.map(fun (x) -> x + 1)(Ok(safeValue))

let final = Ashes.Result.getOrElse(0)(resultValue)

Ashes.IO.print(final)
