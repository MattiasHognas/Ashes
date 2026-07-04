// expect: 505

capability Count =
    | tick : Unit -> Int

let recursive spin = 
    given (i) -> 
        given (acc) -> 
            if i >= 5
            then acc
            else spin(i + 1)(acc + Count.tick(Unit))

let result = 
    handle spin(0)(0) with
        | Count.tick(_) -> 
            let r = resume(1)
            in r + 100
        | return(r) -> r

Ashes.IO.print(result)
