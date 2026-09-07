// expect: 15
Ashes.IO.print(match (let _ = await Ashes.Task.sleep(5)
in
    match await async 10 with
        | Error(_) -> 0
        | Ok(a) ->
            let _ = await Ashes.Task.sleep(5)
            in
                match await async 5 with
                    | Error(_) -> 0
                    | Ok(b) -> a + b)
|> async
|> Ashes.Task.run with
    | Ok(n) -> n
    | Error(_) -> 0)
