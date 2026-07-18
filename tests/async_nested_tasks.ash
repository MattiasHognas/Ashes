// expect: 7
Ashes.IO.print(match Ashes.Task.run(async(let inner =
    async(match await async 3 with
        | Error(_) -> 0
        | Ok(a) -> a + 4)
in
    match await inner with
        | Error(_) -> 0
        | Ok(n) -> n)) with
    | Ok(n) -> n
    | Error(_) -> 0)
