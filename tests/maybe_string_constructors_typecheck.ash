// expect: ok
let a = None
in 
    let b = Some("x")
    in Ashes.IO.writeLine("ok")
