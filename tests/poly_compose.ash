// expect: ok
let compose f g x = f(g(x))
in 
    let add1 n = n + 1
    in 
        let add2 n = n + 2
        in 
            let _a = compose(add2)(add1)(1)
            in 
                let bang s = s + "!"
                in 
                    let wrap s = "[" + s + "]"
                    in 
                        let _b = compose(bang)(wrap)("x")
                        in Ashes.IO.print("ok")
