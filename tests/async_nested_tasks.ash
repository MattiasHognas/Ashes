// expect: 7
Ashes.IO.print(match Ashes.Async.run(async
    let inner = 
        async
            let a = 
                await (async
                    3)
            in a + 4
    in await inner) with
    | Ok(n) -> n
    | Error(_) -> 0)
