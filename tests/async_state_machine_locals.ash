// expect: 111
Ashes.IO.print(match Ashes.Async.run(async(match await async 1 with
    | Error(_) -> 0
    | Ok(a) -> 
        match await async 10 with
            | Error(_) -> 0
            | Ok(b) -> 
                match await async 100 with
                    | Error(_) -> 0
                    | Ok(c) -> a + b + c)) with
    | Ok(n) -> n
    | Error(_) -> 0)
