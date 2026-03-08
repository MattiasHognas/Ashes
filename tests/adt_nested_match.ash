// expect: 10
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
    let inner = Some(Some(10))
    in 
        let outer = 
            match inner with
                | None -> None
                | Some(x) -> x
        in Ashes.IO.print(unwrapOr(outer)(0))
