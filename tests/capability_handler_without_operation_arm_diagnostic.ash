// A handler with only a return arm handles no operation and is rejected.
// expect-compile-error: ASH020 A handler must have at least one operation arm.
capability Clock =
    | now : Unit -> Int

let result =
    handle 1 with
        | return(value) -> value

Ashes.IO.print(result)
