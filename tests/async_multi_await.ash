// expect: 60
Ashes.IO.print(match Ashes.Async.run(async
    let a =
        await (async
            10)
    in
        let b =
            await (async
                20)
        in
            let c =
                await (async
                    30)
            in a + b + c) with
    | Ok(n) -> n
    | Error(_) -> 0)
