import Ashes.Maybe
let inc = 
    fun (x) -> x + 1
in 
    let value = Maybe.map(inc)(Some(41))
    in Ashes.IO.print(Maybe.default(0)(value))
