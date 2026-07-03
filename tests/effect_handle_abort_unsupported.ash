// expect-compile-error: aborting arms need unwinding

effect Clock =
    | now : Unit -> Int

let r = 
    handle Clock.now(Unit) with
        | Clock.now(_) -> 5
        | return(x) -> x

Ashes.IO.print(r)
