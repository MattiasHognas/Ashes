capability Clock =
    | now : Unit -> Int

let frozen = 
    given (work) -> 
        handle work(Unit) with
            | Clock.now(_) -> resume(777)
            | return(r) -> r
