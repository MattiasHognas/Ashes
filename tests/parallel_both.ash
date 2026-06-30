// expect: 7|hello|1,2,3,|499500
import Ashes.Parallel
import Ashes.Text
import Ashes.IO
let rec showList xs = 
    match xs with
        | [] -> ""
        | h :: t -> Ashes.Text.fromInt(h) + "," + showList(t)

let rec psum lo hi = 
    if hi - lo <= 1
    then lo
    else 
        let mid = lo + (hi - lo) / 2
        in 
            match Ashes.Parallel.both(fun (u) -> psum(lo)(mid))(fun (u) -> psum(mid)(hi)) with
                | (a, b) -> a + b

let scalars = 
    match Ashes.Parallel.both(fun (u) -> 3 + 4)(fun (u) -> "hello") with
        | (n, s) -> Ashes.Text.fromInt(n) + "|" + s

let lists = 
    match Ashes.Parallel.both(fun (u) -> 0)(fun (u) -> 1 :: 2 :: 3 :: []) with
        | (z, xs) -> showList(xs)
in Ashes.IO.print(scalars + "|" + lists + "|" + Ashes.Text.fromInt(psum(0)(1000)))
