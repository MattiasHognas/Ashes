// expect: 6
let task = 
    async(match await Ashes.Async.all([async 1, async 2]) with
        | Error(_) -> 0
        | Ok(pair) -> 
            let sum = 
                match pair with
                    | a :: b :: [] -> a + b
                    | _ -> 0
            in 
                match await Ashes.Async.all([Ashes.Async.fromResult(Ok(sum)), async 3]) with
                    | Error(_) -> 0
                    | Ok(both) -> 
                        match both with
                            | a :: b :: [] -> a + b
                            | _ -> 0)
in 
    Ashes.IO.print(match Ashes.Async.run(task) with
        | Ok(n) -> n
        | Error(_) -> 0)
