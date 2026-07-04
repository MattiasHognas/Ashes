// expect: 7|hello|1,2,3,|499500
import Ashes.Parallel
import Ashes.Text
import Ashes.IO
let recursive showList xs = 
    match xs with
        | [] -> ""
        | h :: t -> Ashes.Text.fromInt(h) + "," + showList(t)

let recursive psum lo hi = 
    if hi - lo <= 1
    then lo
    else 
        let mid = lo + (hi - lo) / 2
        in 
            match Ashes.Parallel.both(given (u) -> psum(lo)(mid))(given (u) -> psum(mid)(hi)) with
                | (a, b) -> a + b

let scalars = 
    match Ashes.Parallel.both(given (u) -> 3 + 4)(given (u) -> "hello") with
        | (n, s) -> Ashes.Text.fromInt(n) + "|" + s

let lists = 
    match Ashes.Parallel.both(given (u) -> 0)(given (u) -> 1 :: 2 :: 3 :: []) with
        | (z, xs) -> showList(xs)
in Ashes.IO.print(scalars + "|" + lists + "|" + Ashes.Text.fromInt(psum(0)(1000)))
