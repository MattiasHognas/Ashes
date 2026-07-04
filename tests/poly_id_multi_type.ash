// expect: ok
let id x = x
in 
    let intId = 
        id(given (n) -> n)
    in 
        let strId = 
            id(given (s) -> s)
        in 
            let _a = intId(1)
            in 
                let _b = strId("x")
                in Ashes.IO.print("ok")
