// expect: 3
let rec countDown = 
    fun (n) -> 
        async
            if n == 0
            then 0
            else 
                let x = await countDown(n - 1)
                in x + 1
in 
    Ashes.IO.print(match Ashes.Async.run(countDown(3)) with
        | Ok(n) -> n
        | Error(_) -> 0)
