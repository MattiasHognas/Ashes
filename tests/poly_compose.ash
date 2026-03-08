// expect: ok
let compose = 
    fun (f) -> 
        fun (g) -> 
            fun (x) -> f(g(x))
in 
    let add1 = 
        fun (n) -> n + 1
    in 
        let add2 = 
            fun (n) -> n + 2
        in 
            let _a = compose(add2)(add1)(1)
            in 
                let bang = 
                    fun (s) -> s + "!"
                in 
                    let wrap = 
                        fun (s) -> "[" + s + "]"
                    in 
                        let _b = compose(bang)(wrap)("x")
                        in Ashes.IO.print("ok")
