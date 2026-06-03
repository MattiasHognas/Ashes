// expect: 42
let task = 
    async
        await Ashes.Async.race([Ashes.Async.task(42), Ashes.Async.task(99)])
in 
    Ashes.IO.print(match Ashes.Async.run(task) with
        | Ok(n) -> n
        | Error(_) -> 0)
