let x = Some([1])
in 
    Ashes.IO.print(match x with
        | None -> 0
        | Some([]) -> 1
        | Some(_ :: rest) -> 2)
