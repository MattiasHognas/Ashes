// expect: 3
let task = 
    async
        let result = 
            await Ashes.Async.all([async
                1, async
                2])
        in 
            match result with
                | a :: b :: [] -> a + b
                | _ -> 0
in 
    Ashes.IO.print(match Ashes.Async.run(task) with
        | Ok(n) -> n
        | Error(_) -> 0)
