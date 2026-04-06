// expect: 42
let task = 
    async
        await Ashes.Async.race([async
            42, async
            99])
in 
    Ashes.IO.print(match Ashes.Async.run(task) with
        | Ok(n) -> n
        | Error(_) -> 0)
