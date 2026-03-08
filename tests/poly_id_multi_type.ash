// expect: ok
let id = 
    fun (x) -> x
in 
    let intId = 
        id(fun (n) -> n)
    in 
        let strId = 
            id(fun (s) -> s)
        in 
            let _a = intId(1)
            in 
                let _b = strId("x")
                in Ashes.IO.print("ok")
