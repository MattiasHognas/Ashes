// expect: 101

capability Ask =
    | ask : Unit -> Int

let inner = 
    given (u) -> 
        handle Ask.ask(Unit) with
            | Ask.ask(_) -> 
                let outerValue = Ask.ask(Unit)
                in resume(outerValue + 1)
            | return(r) -> r

let result = 
    handle inner(Unit) with
        | Ask.ask(_) -> resume(100)
        | return(r) -> r

Ashes.IO.print(result)
