// expect: 42
let task = 
    async(let x = 40
    in 
        match await Ashes.Async.all([async 1, async 1]) with
            | Error(_) -> 0
            | Ok(result) -> 
                match result with
                    | a :: b :: [] -> x + a + b
                    | _ -> 0)
in 
    Ashes.IO.print(match Ashes.Async.run(task) with
        | Ok(n) -> n
        | Error(_) -> 0)
