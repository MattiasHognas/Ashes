// expect-compile-error: 'handle' expressions are not yet supported

effect Clock =
    | now : Unit -> Int

let x = 
    handle Clock.now(Unit) with
        | Clock.now(_) -> resume(42)
        | return(r) -> r

Ashes.IO.print(x)
