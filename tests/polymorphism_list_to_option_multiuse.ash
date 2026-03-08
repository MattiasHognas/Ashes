// expect: ok
type Option =
    | None
    | Some(T)

let firstOrNone = 
    fun (xs) -> 
        match xs with
            | [] -> None
            | x :: _ -> Some(x)
in 
    let _a = firstOrNone([1, 2, 3])
    in 
        let _b = firstOrNone(["a", "b"])
        in Ashes.IO.print("ok")
