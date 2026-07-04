// expect: 100000

capability Step =
    | bump : Int -> Int

let recursive loop = 
    given (i) -> 
        given (acc) -> 
            if i >= 100000
            then acc
            else loop(i + 1)(Step.bump(acc))

let result = 
    handle loop(0)(0) with
        | Step.bump(x) -> resume(x + 1)
        | return(r) -> r

Ashes.IO.print(result)
