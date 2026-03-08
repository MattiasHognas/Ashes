// expect: 1
type Result =
    | Ok(T)
    | Error(T)

let getOrDefault = 
    fun (res) -> 
        fun (def) -> 
            match res with
                | Ok(x) -> x
                | Error(_) -> def
in Ashes.IO.print(getOrDefault(Ok(1))(0))
