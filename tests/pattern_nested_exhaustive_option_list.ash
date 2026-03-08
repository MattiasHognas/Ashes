// expect: 2
type Option =
    | None
    | Some(T)

let x = Some([])
in 
    Ashes.IO.print(match x with
        | None -> 0
        | Some([]) -> 2
        | Some(_ :: rest) -> 1)
