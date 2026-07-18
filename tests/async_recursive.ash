// expect: 3
Ashes.IO.print(match Ashes.Task.run(async(let recursive countDown n =
    if n == 0
    then 0
    else countDown(n - 1) + 1
in countDown(3))) with
    | Ok(n) -> n
    | Error(_) -> 0)
