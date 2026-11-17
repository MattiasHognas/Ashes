// Known deferred Core query-context gap; not a passing stage1 fixture.
capability Clock =
    | now : Unit -> Int

let recursive same left right = left == right

let result =
    handle same(Clock.now(Unit))(7) with
        | Clock.now(_) -> resume(7)
        | return(value) -> value

if result
then Ashes.IO.print("capability query passed")
else Ashes.IO.panic("capability query produced the wrong result")
