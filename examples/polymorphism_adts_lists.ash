type Option =
    | None
    | Some(T)

type Result =
    | Ok(T)
    | Error(T)

let firstOr = 
    fun (xs) -> 
        fun (def) -> 
            match xs with
                | [] -> def
                | x :: _ -> x
in 
    let unwrapOr = 
        fun (opt) -> 
            fun (def) -> 
                match opt with
                    | None -> def
                    | Some(x) -> x
    in 
        let _a = firstOr([1, 2, 3])(0)
        in 
            let _b = firstOr(["a", "b"])("z")
            in 
                let _c = unwrapOr(Some(10))(0)
                in 
                    let _d = unwrapOr(None)("fallback")
                    in 
                        let _r1 = Ok(firstOr([4, 5])(0))
                        in 
                            let _r2 = Error(firstOr([])(0))
                            in Ashes.IO.print("ok")
