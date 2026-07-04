// expect-compile-error: ASH027
capability Clock =
    | now : Unit -> Int

provide Clock =
    | now = given (_) -> 1

let r =
    handle Clock.now(Unit) with
        | Clock.now(_) -> resume(2)
        | return(x) -> x

Ashes.IO.print(r)
