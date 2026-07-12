import Ashes.IO
import Ashes.List
import Ashes.Maybe
import Ashes.Result
let nums = [1, 2, 3, 4, 5]

let doubled =
    List.map(given (x) -> x * 2)(nums)

let large =
    List.filter(given (x) -> x >= 6)(doubled)

let count = List.length(large)

let maybeTop = List.head(List.reverse(large))

let maybeAdjusted =
    Maybe.map(given (x) -> x + count)(maybeTop)

let safeValue =
    if Maybe.isSome(maybeAdjusted)
    then Maybe.getOrElse(0)(maybeAdjusted)
    else 0

let resultValue =
    Result.map(given (x) -> x + 1)(Ok(safeValue))

if Result.isOk(resultValue)
then
    match resultValue with
        | Ok(value) -> print(value)
        | Error(_) -> print(0)
else print(0)
