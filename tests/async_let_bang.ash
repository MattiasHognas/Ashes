// expect: 3
Ashes.IO.print(match Ashes.Async.run(async
    let a = 
        await (async
            1)
    in 
        let b = 
            await (async
                2)
        in a + b) with
    | Ok(n) -> n
    | Error(_) -> 0)
