import Option
let inc = 
    fun (x) -> x + 1
in 
    let value = Option.map(inc)(Some(41))
    in Ashes.IO.print(Option.default(0)(value))
