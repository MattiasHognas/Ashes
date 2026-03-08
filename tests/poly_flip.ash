// expect: ok
let flip = 
    fun (f) -> 
        fun (b) -> 
            fun (a) -> f(a)(b)
in 
    let keepLeft = 
        fun (a) -> 
            fun (b) -> a
    in 
        let _a = flip(keepLeft)(1)("x")
        in 
            let keepRight = 
                fun (a) -> 
                    fun (b) -> b
            in 
                let _b = flip(keepRight)(true)(0)
                in Ashes.IO.print("ok")
