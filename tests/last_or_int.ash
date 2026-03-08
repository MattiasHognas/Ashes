// expect: 3
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
in Ashes.IO.print(lastOr([1, 2, 3])(0))
