import Ashes.Map
import Ashes.IO
import Ashes.Text
let cmp a b = 
    if a == b
    then 0
    else 
        if a <= b
        then -1
        else 1

let getv m k = 
    match Ashes.Map.get(cmp)(k)(m) with
        | None -> -1
        | Some(v) -> v

let rec innerFold i lim m = 
    if i > lim
    then m
    else innerFold(i + 1)(lim)(Ashes.Map.set(cmp)(i)(i * 7)(m))

let rec rounds r acc m = 
    if r <= 0
    then acc + getv(m)(1)
    else 
        let w = Ashes.Map.set(cmp)(0)(0)(m)
        in 
            let folded = innerFold(r * 1000)(r * 1000 + 999)(w)
            in rounds(r - 1)(acc + getv(w)(0))(folded)

let base = innerFold(0)(999)(Ashes.Map.empty)

let result = rounds(12)(0)(base)

Ashes.IO.print(Ashes.Text.fromInt(result))
