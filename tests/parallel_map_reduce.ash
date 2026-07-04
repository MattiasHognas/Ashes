// expect: 1|124750|249500|0,1,2,3,4,5,6,7,8,9,
import Ashes.Parallel
import Ashes.Text
import Ashes.IO
let recursive range lo hi = 
    if lo >= hi
    then []
    else lo :: range(lo + 1)(hi)

let nums = range(0)(500)

let recursive seqMap f xs = 
    match xs with
        | [] -> []
        | h :: t -> f(h) :: seqMap(f)(t)

let recursive eq xs ys = 
    match xs with
        | [] -> 
            match ys with
                | [] -> 1
                | _ -> 0
        | a :: at -> 
            match ys with
                | [] -> 0
                | b :: bt -> 
                    if a == b
                    then eq(at)(bt)
                    else 0

let mapPar = 
    Ashes.Parallel.map(given (x) -> x * 2)(nums)

let mapSeq = 
    seqMap(given (x) -> x * 2)(nums)

let sumPar = 
    Ashes.Parallel.reduce(given (a) -> 
        given (b) -> a + b)(0)(given (x) -> x)(nums)

let sumDoublePar = 
    Ashes.Parallel.reduce(given (a) -> 
        given (b) -> a + b)(0)(given (x) -> x * 2)(nums)

let strsPar = 
    Ashes.Parallel.map(given (x) -> Ashes.Text.fromInt(x))(range(0)(10))

let recursive joinStr xs = 
    match xs with
        | [] -> ""
        | h :: t -> h + "," + joinStr(t)
in Ashes.IO.print(Ashes.Text.fromInt(eq(mapPar)(mapSeq)) + "|" + Ashes.Text.fromInt(sumPar) + "|" + Ashes.Text.fromInt(sumDoublePar) + "|" + joinStr(strsPar))
