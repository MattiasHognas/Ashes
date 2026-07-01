// expect: 3494400
import Ashes.Parallel
import Ashes.Text
import Ashes.IO
let rec range lo hi = 
    if lo >= hi
    then []
    else lo :: range(lo + 1)(hi)

let base = range(0)(64)

let rec sumList xs = 
    match xs with
        | [] -> 0
        | h :: t -> h + sumList(t)

let rec loop i acc = 
    if i <= 0
    then acc
    else 
        loop(i - 1)(acc + sumList(Ashes.Parallel.map(fun (x) -> x + i)(base)))
in Ashes.IO.print(Ashes.Text.fromInt(loop(300)(0)))
