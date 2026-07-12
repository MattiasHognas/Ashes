// expect: ok
let flip f b a = f(a)(b)
in
    let keepLeft a b = a
    in
        let _a = flip(keepLeft)(1)("x")
        in
            let keepRight a b = b
            in
                let _b = flip(keepRight)(true)(0)
                in Ashes.IO.print("ok")
