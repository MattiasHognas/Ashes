// expect-compile-error: must handle operation 'set'

capability State(a) =
    | get : Unit -> a
    | set : a -> Unit

let r = 
    handle State.get(Unit) + 1 with
        | State.get(_) -> resume(41)
        | return(r) -> r

Ashes.IO.print(r)
