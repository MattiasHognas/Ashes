let id = 
    fun (x) -> x
in 
    let _a = id(1)
    in 
        let _b = id("x")
        in 
            let _c = id(true)
            in Ashes.IO.print("ok")
