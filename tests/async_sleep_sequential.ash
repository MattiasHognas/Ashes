// expect: 42
Ashes.IO.print(match Ashes.Async.run(async
    let _ = await Ashes.Async.sleep(5)
    in 
        let _ = await Ashes.Async.sleep(5)
        in 42) with
    | Ok(n) -> n
    | Error(_) -> 0)
