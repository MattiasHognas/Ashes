// expect: c
let rec lastOr = 
    fun (xs) -> 
        fun (default) -> 
            let rec loop = 
                fun (ys) -> 
                    match ys with
                        | [] -> default
                        | x :: rest -> 
                            match rest with
                                | [] -> x
                                | _ -> loop(rest)
            in loop(xs)
in Ashes.IO.print(lastOr(["a", "b", "c"])(""))
