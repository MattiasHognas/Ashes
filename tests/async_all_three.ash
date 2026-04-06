// expect: 6
let task = 
    async
        let results = 
            await Ashes.Async.all([async
                1, async
                2, async
                3])
        in 
            match results with
                | a :: b :: c :: [] -> a + b + c
                | _ -> 0
in 
    Ashes.IO.print(match Ashes.Async.run(task) with
        | Ok(n) -> n
        | Error(_) -> 0)
