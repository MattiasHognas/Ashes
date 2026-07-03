// expect-compile-error: one-shot resumptive handlers are not yet supported

effect Log =
    | log : Str -> Unit

let r = 
    handle Log.log("x") with
        | Log.log(msg) -> 
            match resume(Unit) with
                | u -> u
        | return(r) -> r

Ashes.IO.print("no")
