// expect: 7
Ashes.IO.print(match (let inner =
    async(match await async 3 with
        | Error(_) -> 0
        | Ok(a) -> a + 4)
in
    match await inner with
        | Error(_) -> 0
        | Ok(n) -> n)
|> async
|> Ashes.Task.run with
    | Ok(n) -> n
    | Error(_) -> 0)
