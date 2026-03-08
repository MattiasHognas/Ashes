// stdin: a\nb\n
// expect: a:b
let first = Ashes.IO.readLine(Unit)
in 
    let second = Ashes.IO.readLine(Unit)
    in 
        match first with
            | None -> Ashes.IO.print("missing-first")
            | Some(a) -> 
                match second with
                    | Some(b) -> Ashes.IO.print(a + ":" + b)
                    | None -> Ashes.IO.print("missing-second")
