// expect: 1
let task = 
    async
        let results = await Ashes.Async.all([])
        in 
            match results with
                | [] -> 1
                | _ -> 0
in 
    Ashes.IO.print(match Ashes.Async.run(task) with
        | Ok(n) -> n
        | Error(_) -> 0)
