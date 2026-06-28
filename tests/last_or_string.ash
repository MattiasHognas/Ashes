// expect: c
let rec lastOr xs default = 
    let rec loop ys = 
        match ys with
            | [] -> default
            | x :: rest -> 
                match rest with
                    | [] -> x
                    | _ -> loop(rest)
    in loop(xs)
in Ashes.IO.print(lastOr(["a", "b", "c"])(""))
