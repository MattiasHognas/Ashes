// expect: ok
let headOr = 
    fun (xs) -> 
        fun (def) -> 
            match xs with
                | [] -> def
                | x :: _ -> x
in 
    let _a = headOr([1, 2, 3])(0)
    in 
        let _b = headOr(["a", "b"])("z")
        in Ashes.IO.print("ok")
