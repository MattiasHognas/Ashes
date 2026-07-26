// expect: clock:1234 done:ba

capability Clock =
    | now : Unit -> Int

provide Clock =
    | now =
        given (_) -> 1234

capability Trace =
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

let clockValue = Clock.now(Unit)
in Ashes.IO.print("clock:" + Ashes.Text.fromInt(clockValue) + " " + result)
