// expect: 123,1
capability Clock =
    | now : Unit -> Int

capability Ord(a) =
    | compare : a -> a -> Int

provide Ord(Int) =
    | compare = 
        given (a) -> 
            given (b) -> a - b

let recursive minOf : a -> List(a) -> a needs {Ord(a)} = 
    given (best) -> 
        given (items) -> 
            match items with
                | [] -> best
                | x :: rest -> 
                    if Ord.compare(x)(best) < 0
                    then minOf(x)(rest)
                    else minOf(best)(rest)

let stampedMin : List(a) -> (Int, a) needs {Clock, Ord(a)} = 
    given (items) -> 
        let t = Clock.now(Unit)
        in 
            match items with
                | [] -> (t, minOf(0)([]))
                | x :: rest -> (t, minOf(x)(rest))

let result = 
    handle stampedMin([5, 3, 9, 1, 7]) with
        | Clock.now(_) -> resume(123)
        | return(r) -> r

match result with
    | (t, m) -> Ashes.IO.print(Ashes.Text.fromInt(t) + "," + Ashes.Text.fromInt(m))
