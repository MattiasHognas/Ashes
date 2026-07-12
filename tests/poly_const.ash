// expect: ok
let const x y = x
in
    let _a = const(1)("ignore")
    in
        let _b = const("keep")(0)
        in Ashes.IO.print("ok")
