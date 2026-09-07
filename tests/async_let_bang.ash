// expect: 3
Ashes.IO.print(match (match await async 1 with
    | Error(_) -> 0
    | Ok(a) ->
        match await async 2 with
            | Error(_) -> 0
            | Ok(b) -> a + b)
|> async
|> Ashes.Task.run with
    | Ok(n) -> n
    | Error(_) -> 0)
