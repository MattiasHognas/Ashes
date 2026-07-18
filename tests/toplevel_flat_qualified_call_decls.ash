// expect: 14
import Ashes.IO
import Ashes.Collection.List
import Ashes.Core.Maybe
import Ashes.Core.Result
let nums = [1, 2, 3, 4, 5]

let doubled =
    Ashes.Collection.List.map(given (x) -> x * 2)(nums)

let large =
    Ashes.Collection.List.filter(given (x) -> x >= 6)(doubled)

let count = Ashes.Collection.List.length(large)

let maybeTop = Ashes.Collection.List.head(Ashes.Collection.List.reverse(large))

let adjusted =
    Ashes.Core.Maybe.map(given (x) -> x + count)(maybeTop)

let safeValue = Ashes.Core.Maybe.getOrElse(0)(adjusted)

let resultValue =
    Ashes.Core.Result.map(given (x) -> x + 1)(Ok(safeValue))

let final = Ashes.Core.Result.getOrElse(0)(resultValue)

Ashes.IO.print(final)
