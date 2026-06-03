// expect: 60
Ashes.IO.print(match Ashes.Async.run(async
    match await (async
        10) with
        | Error(_) -> 0
        | Ok(a) ->
            match await (async
                20) with
                | Error(_) -> 0
                | Ok(b) ->
                    match await (async
                        30) with
                        | Error(_) -> 0
                        | Ok(c) -> a + b + c) with
    | Ok(n) -> n
    | Error(_) -> 0)
