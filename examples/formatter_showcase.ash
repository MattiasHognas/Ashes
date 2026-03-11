let rec compute fallback xs = 
    match xs with
        | [] -> 
            if fallback >= 10
            then Ok(fallback)
            else Error("small")
        | head :: tail -> 
            let next = 
                if head >= fallback
                then head + fallback
                else fallback - head
            in compute(next)(tail)
in 
    match compute(0)([4, 3, 9]) with
        | Ok(value) -> Ashes.IO.print(value)
        | Error(_) -> Ashes.IO.print(0)
