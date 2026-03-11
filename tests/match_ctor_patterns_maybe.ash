// expect: 12
let unwrapOr = 
    fun (opt) -> 
        fun (def) -> 
            match opt with
                | None -> def
                | Some(x) -> x
in 
    let zero = 
        match Some(0) with
            | Some(x) -> x
            | None -> 0
    in 
        let resTag = 
            match Error(1) with
                | Ok(x) -> 1
                | Error(x) -> 2
        in Ashes.IO.print(unwrapOr(Some(10))(0) + zero + resTag)
