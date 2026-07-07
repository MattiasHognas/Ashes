// expect: 3
import Ashes.IO
import Ashes.List
capability Tag =
    | tag : Int -> Int

let tagged = 
    handle Ashes.List.map(given (x) -> Tag.tag(x))(10 :: 20 :: 30 :: []) with
        | Tag.tag(n) -> resume(n)
        | return(r) -> r

Ashes.IO.print(Ashes.List.length(tagged))
