// expect: done:ba

effect Trace =
    | note : Str -> Unit

let work = 
    given (u) -> 
        let _ = Trace.note("a")
        in 
            let _ = Trace.note("b")
            in "done:"

let result = 
    handle work(Unit) with
        | Trace.note(msg) -> 
            match resume(Unit) with
                | acc -> acc + msg
        | return(r) -> r

Ashes.IO.print(result)
