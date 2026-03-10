// expect: 3
let x = 1
in 
    let y = 
        let? x = Ok(2)
        in Ok(x + 1)
    in 
        match y with
            | Ok(v) -> Ashes.IO.print(v)
            | Error(_) -> Ashes.IO.print(0)
