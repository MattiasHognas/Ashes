// expect: 15
Ashes.IO.print(match Ashes.Async.run(async(let _ = await Ashes.Async.sleep(5)
in 
    match await async 10 with
        | Error(_) -> 0
        | Ok(a) -> 
            let _ = await Ashes.Async.sleep(5)
            in 
                match await async 5 with
                    | Error(_) -> 0
                    | Ok(b) -> a + b)) with
    | Ok(n) -> n
    | Error(_) -> 0)
