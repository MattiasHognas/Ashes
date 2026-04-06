// expect: 111
Ashes.IO.print(match Ashes.Async.run(async
    let a =
        await (async
            1)
    in
        let b =
            await (async
                10)
        in
            let c =
                await (async
                    100)
            in a + b + c) with
    | Ok(n) -> n
    | Error(_) -> 0)
