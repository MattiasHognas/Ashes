// expect: true
let f =
    let recursive invert x = !x
    in invert
in
    Ashes.IO.print(if f(false)
    then "true"
    else "false")
