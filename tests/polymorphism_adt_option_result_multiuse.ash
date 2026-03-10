// expect: ok
type Option =
    | None
    | Some(T)

let unwrapOr = 
    fun (opt) -> 
        fun (def) -> 
            match opt with
                | None -> def
                | Some(x) -> x
in 
    let getOrDefault = 
        fun (res) -> 
            fun (def) -> 
                match res with
                    | Ok(x) -> x
                    | Error(_) -> def
    in 
        let _a = unwrapOr(Some(1))(0)
        in 
            let _b = unwrapOr(None)("fallback")
            in 
                let _c = getOrDefault(Ok("value"))("default")
                in 
                    let _d = getOrDefault(Error(42))(0)
                    in Ashes.IO.print("ok")
