// expect: 3
Ashes.IO.print(match Ashes.Async.run(async(match await async 1 with
    | Error(_) -> 0
    | Ok(a) ->
        match await async 2 with
            | Error(_) -> 0
            | Ok(b) -> a + b)) with
    | Ok(n) -> n
    | Error(_) -> 0)
