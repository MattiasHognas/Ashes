// expect: 3
let task = 
    async(match await Ashes.Async.all([async 1, async 2]) with
        | Error(_) -> 0
        | Ok(result) -> 
            match result with
                | a :: b :: [] -> a + b
                | _ -> 0)
in 
    Ashes.IO.print(match Ashes.Async.run(task) with
        | Ok(n) -> n
        | Error(_) -> 0)
