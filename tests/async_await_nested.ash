// expect: 10
Ashes.IO.print(match Ashes.Async.run(async
    let x = 
        await (async
            10)
    in x) with
    | Ok(n) -> n
    | Error(_) -> 0)
