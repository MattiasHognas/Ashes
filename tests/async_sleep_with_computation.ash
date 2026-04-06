// expect: 15
Ashes.IO.print(match Ashes.Async.run(async
    let _ = await Ashes.Async.sleep(5)
    in
        let a = await (async 10)
        in
            let _ = await Ashes.Async.sleep(5)
            in
                let b = await (async 5)
                in a + b) with
    | Ok(n) -> n
    | Error(_) -> 0)
