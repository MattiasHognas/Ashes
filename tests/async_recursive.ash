// expect: 3
Ashes.IO.print(match (let recursive countDown n =
    if n == 0
    then 0
    else countDown(n - 1) + 1
in countDown(3))
|> async
|> Ashes.Task.run with
    | Ok(n) -> n
    | Error(_) -> 0)
